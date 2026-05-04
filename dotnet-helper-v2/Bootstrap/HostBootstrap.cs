using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autofac;
using Hydac.PDT.Business.Contracts.CodeBuilder.Details;
using Hydac.PDT.PdtFramework;

namespace MatchPdt.Helper.Bootstrap
{
    /// <summary>
    /// Boots PDT's headless host the same way the CLI does, then loads a project.
    ///
    /// The CLI's bootstrap entry is <c>CliSetup.CreateApplication()</c>. It is internal,
    /// but it is the only call we need: it wires every surrogate the container expects
    /// (IConsoleLogger, IDispatcherAdapter, IApplicationSettings factory, IMessageBoxAdapter,
    /// ICliPathDefinitions, etc.) before calling InitializeApplicationSettings/Main/etc.
    /// Replicating its 13-surrogate registration block ourselves would be ~50 lines of
    /// brittle code that drifts on every PDT update; one reflection call is cheaper.
    ///
    /// After bootstrap, ServiceLocator is public — we use it directly for resolves.
    /// ICliProjectCommands is internal, so resolving it goes through reflection too.
    /// </summary>
    internal sealed class HostBootstrap : IDisposable
    {
        public string SmokeTestServiceTypeName { get; }
        public object Project { get; }
        public FileInfo HdbFile { get; }
        public string PristineSnapshotPath { get; }

        private readonly Type _cliInitializationType;
        private readonly object _cliProjectCommands;
        private readonly Type _cliCommandsIface;
        private bool _disposed;

        private HostBootstrap(Type cliInitializationType, object project, string smokeTestServiceTypeName,
            FileInfo hdbFile, object cliProjectCommands, Type cliCommandsIface, string pristineSnapshotPath)
        {
            _cliInitializationType = cliInitializationType;
            Project = project;
            SmokeTestServiceTypeName = smokeTestServiceTypeName;
            HdbFile = hdbFile;
            _cliProjectCommands = cliProjectCommands;
            _cliCommandsIface = cliCommandsIface;
            PristineSnapshotPath = pristineSnapshotPath;
        }

        public static HostBootstrap Initialize(FileInfo hdbFile)
        {
            var sw = Stopwatch.StartNew();

            // Snapshot the pristine .hdb before PDT touches it. SaveProject merges any
            // entries PDT didn't write back from this snapshot — see the comment in
            // SaveProject for why.
            var pristineSnapshotPath = Path.Combine(
                Path.GetTempPath(),
                $"match_pdt_helper_pristine_{Guid.NewGuid():N}.hdb");
            File.Copy(hdbFile.FullName, pristineSnapshotPath, overwrite: true);
            Program.WriteLog($"Pristine snapshot at {pristineSnapshotPath}");

            // Force-load Cli.Business; AppDomain.AssemblyResolve will fan out from here
            // assuming the helper's WorkingDirectory is the PDT install dir.
            var cliBusiness = Assembly.Load("Hydac.PDT.Cli.Business");
            Program.WriteLog($"Loaded {cliBusiness.GetName().Name} v{cliBusiness.GetName().Version} from {cliBusiness.Location}");

            var cliSetupType = cliBusiness.GetType(
                "Hydac.PDT.Cli.Business.Environment.CliSetup", throwOnError: true)!;
            var cliInitType = cliBusiness.GetType(
                "Hydac.PDT.Cli.Business.Environment.CliInitialization", throwOnError: true)!;
            var cliCommandsIface = cliBusiness.GetType(
                "Hydac.PDT.Cli.Business.Environment.ICliProjectCommands", throwOnError: true)!;

            // 1. Run the bootstrap manually instead of calling CliSetup.CreateApplication
            //    directly, so we can wedge our own customRegister callback in. We need to
            //    override IApplicationVersionDetails to report the real loaded PDT version
            //    — without this, the helper's AssemblyVersion (1.0.0.0 by default) is what
            //    PDT compares against, which both makes load fail (project newer than
            //    helper) AND makes save write our version into info.xml's PdtVersionString,
            //    breaking subsequent reloads.
            //
            //    The replicated bootstrap mirrors CliSetup.CreateApplication:
            //      1. CliInitialization.CreateApp(register)
            //         where register = CliSetup.RegisterCustomDependencies + our overrides
            //      2. CliInitialization.InitializeApplicationSettings
            //      3. KnowledgeBaseAgentInitialization.InitializeKnowledgeAgentAsync().Wait()
            //      4. CliInitialization.InitializeMain
            //      5. CliInitialization.InitializeProjectConditioner
            Program.WriteLog("Bootstrapping PDT host (replicated CliSetup with version override)...");
            var pdtVersion = ReadLoadedPdtVersion();
            Program.WriteLog($"Real PDT version: {pdtVersion}");

            var pdtSurrogateRegister = cliSetupType.GetMethod("RegisterCustomDependencies",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("CliSetup.RegisterCustomDependencies");

            Action<ContainerBuilder> combinedRegister = builder =>
            {
                // Invoke PDT's surrogate registration via reflection (Delegate.CreateDelegate
                // doesn't bind to internal methods due to transparency rules).
                pdtSurrogateRegister.Invoke(null, new object[] { builder });

                // Register our IApplicationVersionDetails LAST so Autofac's last-registration-
                // wins picks it up over whatever Hydac.PDT.Business.Implementation registers.
                builder.RegisterInstance(new HostApplicationVersionDetails(pdtVersion))
                    .As<IApplicationVersionDetails>()
                    .SingleInstance();
            };

            var createApp = cliInitType.GetMethod("CreateApp",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("CliInitialization.CreateApp");
            try
            {
                createApp.Invoke(null, new object?[] { combinedRegister });

                cliInitType.GetMethod("InitializeApplicationSettings", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null);

                var kbInitType = Assembly.Load("Hydac.PDT.KnowledgeBase").GetType(
                    "Hydac.PDT.KnowledgeBase.KnowledgeBaseAgentInitialization", throwOnError: true)!;
                var kbInit = kbInitType.GetMethod("InitializeKnowledgeAgentAsync",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingMethodException("KnowledgeBaseAgentInitialization.InitializeKnowledgeAgentAsync");
                ((Task)kbInit.Invoke(null, null)!).GetAwaiter().GetResult();

                cliInitType.GetMethod("InitializeMain", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null);
                cliInitType.GetMethod("InitializeProjectConditioner", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, null);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }
            Program.WriteLog($"Container built and bootstrapped ({sw.ElapsedMilliseconds} ms)");

            // 2. Resolve ICliProjectCommands (internal) by closed-generic reflection on
            //    the public ServiceLocator.Resolve<T>().
            var commands = ResolveByReflection(cliCommandsIface)
                ?? throw new InvalidOperationException("ICliProjectCommands not registered");

            // 3. Load the project. CliProjectCommands.LoadProjectAsync delegates to
            //    TMainVM.LoadProjectCommand.ExecuteAsync — must run on the dispatcher,
            //    which is exactly where this code is running (DispatcherRunner posts
            //    HostBootstrap.Initialize onto the dispatcher).
            var loadAsync = cliCommandsIface.GetMethod("LoadProjectAsync")
                ?? throw new MissingMethodException("ICliProjectCommands.LoadProjectAsync");
            var loadStart = sw.ElapsedMilliseconds;
            var loadTask = (Task)loadAsync.Invoke(commands, new object[] { hdbFile })!;
            loadTask.GetAwaiter().GetResult();
            var resultProp = loadTask.GetType().GetProperty("Result")!;
            var project = resultProp.GetValue(loadTask)
                ?? throw new InvalidOperationException("LoadProjectAsync returned null");
            Program.WriteLog($"Project loaded ({sw.ElapsedMilliseconds - loadStart} ms)");

            // 4. Smoke test: resolve a known business service. MessageService is
            //    registered via its IMessageService interface (Hydac.PDT.Can.Business.Contracts).
            var canContracts = Assembly.Load("Hydac.PDT.Can.Business.Contracts");
            var messageServiceIface = canContracts.GetType(
                "Hydac.PDT.Can.Business.Contracts.IMessageService", throwOnError: false)
                ?? canContracts.GetType("Hydac.PDT.Can.Business.Contracts.Message.IMessageService", throwOnError: false)
                ?? throw new InvalidOperationException("IMessageService type not found in Hydac.PDT.Can.Business.Contracts");
            var messageService = ResolveByReflection(messageServiceIface)
                ?? throw new InvalidOperationException("IMessageService did not resolve — Phase 1 failed");

            // Wait for validation to complete after load. The CLI's BuildExecutor follows
            // the same pattern (LoadProject → WaitForValidationToEnd → Build → Close).
            // Without it, save-time persistence adapters see partially-populated state and
            // the resulting .hdb fails to reload with "given key was not present in the
            // dictionary".
            try
            {
                var waitForValidation = cliCommandsIface.GetMethod("WaitForValidationToEndAsync");
                if (waitForValidation != null)
                {
                    var t = (Task)waitForValidation.Invoke(commands, null)!;
                    t.GetAwaiter().GetResult();
                    Program.WriteLog($"Validation completed ({sw.ElapsedMilliseconds - loadStart} ms total since load)");
                }
            }
            catch (Exception ex)
            {
                Program.WriteLog($"WaitForValidationToEndAsync warning (non-fatal): {ex.Message}");
            }

            // IErrorCollection auto-populates with the project's existing errors during
            // CliInitialization.InitializeMain (via the LoadErrorsFromRepository path).
            // We previously tried to also call ILoadErrorsFromPersistence here, but the
            // auto-load already produces a populated collection (~75 errors on the test
            // project); manual loading would create duplicates and that interface lives
            // in an internal namespace not exposed via Contracts anyway.

            return new HostBootstrap(
                cliInitType,
                project,
                messageService.GetType().FullName ?? "<unknown>",
                hdbFile,
                commands,
                cliCommandsIface,
                pristineSnapshotPath);
        }

        /// <summary>
        /// Save the project back to its original .hdb path. Two things have to be
        /// post-processed because PDT's save isn't aimed at headless tooling:
        ///
        /// 1. info.xml's PdtVersionString is derived from
        ///    <c>Assembly.GetEntryAssembly().GetName().Version</c> in
        ///    <c>DataLayer.AddInfoFile</c> — i.e. our helper's version, not the
        ///    loaded PDT's. If we leave that, the saved project becomes unloadable:
        ///    <c>DataLayerFactory</c> sees an unsupported version and throws
        ///    "given key was not present in the dictionary" from its update-dispatch
        ///    table. We rewrite info.xml after save with the real PDT version.
        ///
        /// 2. <c>IProjectAgent.Save</c> creates a fresh zip and only writes the
        ///    sub-systems that were loaded into memory. Sub-managers like
        ///    Errors/HymlEcuTemplates/CanMessages/etc. lazy-load when their VMs are
        ///    accessed in the UI. Headless, those VMs never run → their .dat files
        ///    silently disappear from the saved archive (we measured 16 of 32 files
        ///    dropped on a clean Project_Test). To make the saved hdb at least
        ///    reloadable, we copy any pre-existing entry that PDT didn't rewrite
        ///    back from the pristine snapshot we took at load.
        ///
        /// This is a backstop, not a real fix: mutations to "carry-forward" files
        /// (e.g. Errors.dat) won't persist with this approach because PDT never
        /// loaded the canonical state. add_custom_error therefore stays gated as
        /// experimental until the per-file persistence pipeline is solved
        /// (force-load each sub-manager so PDT writes them back, or write the
        /// mutation to the .dat file ourselves before calling Save).
        ///
        /// Must be called on the dispatcher thread.
        /// </summary>
        public void SaveProject()
        {
            var businessIfaces = Assembly.Load("Hydac.PDT.Business.Interfaces");
            var projectServiceType = businessIfaces.GetType(
                "Hydac.PDT.Business.Contracts.Project.IProjectService", throwOnError: true)!;
            var projectService = ResolveByReflection(projectServiceType)
                ?? throw new InvalidOperationException("IProjectService not registered");

            var actualAgent = projectServiceType.GetProperty("ActualProjectAgent")!.GetValue(projectService)
                ?? throw new InvalidOperationException("IProjectService.ActualProjectAgent is null — no project loaded");

            var save = actualAgent.GetType().GetMethod("Save", new[] { typeof(string) })
                ?? throw new MissingMethodException("IProjectAgent.Save(string)");
            try
            {
                save.Invoke(actualAgent, new object[] { HdbFile.FullName });
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }

            // ProjectAgent.Save doesn't emit Errors.dat (no persistence adapter is wired
            // for headless flows). Write it explicitly via ISaveErrorsToDataLayer, which
            // serializes IErrorCollection → BinaryFormatter → Errors.dat in the saved
            // .hdb. This must happen AFTER ProjectAgent.Save (which sets DataLayer
            // .FileBackup to the saved .hdb path) and BEFORE the snapshot merge (so the
            // merge sees Errors.dat as already-present and skips the stale carry-forward).
            WriteErrorsDat();

            MergeMissingFilesFromSnapshot(HdbFile.FullName, PristineSnapshotPath);
            PatchInfoXmlVersion(HdbFile.FullName);
        }

        private void WriteErrorsDat()
        {
            try
            {
                var errorsContracts = Assembly.Load("Hydac.PDT.Errors.Business.Contracts");
                var saveType = errorsContracts.GetType(
                    "Hydac.PDT.Errors.Business.Contracts.Loader.ISaveErrorsToDataLayer",
                    throwOnError: true)!;
                var saver = ResolveByReflection(saveType)
                    ?? throw new InvalidOperationException("ISaveErrorsToDataLayer not registered");

                var collectionType = errorsContracts.GetType(
                    "Hydac.PDT.Errors.Business.Contracts.IErrorCollection", throwOnError: true)!;
                var collection = ResolveByReflection(collectionType)
                    ?? throw new InvalidOperationException("IErrorCollection not registered");

                var save = saveType.GetMethod("Save")!;
                save.Invoke(saver, new[] { collection });

                var count = ((System.Collections.IEnumerable)collection).Cast<object>().Count();
                Program.WriteLog($"Wrote Errors.dat with {count} errors");
            }
            catch (Exception ex)
            {
                Program.WriteLog($"WriteErrorsDat warning (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Copy any entries from the pre-load snapshot that PDT didn't write back.
        /// This is the backstop for sub-systems that lazy-load on UI access — see
        /// the SaveProject doc-comment for the broader context.
        /// </summary>
        private static void MergeMissingFilesFromSnapshot(string savedPath, string snapshotPath)
        {
            if (!File.Exists(snapshotPath)) return;

            var savedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var savedZip = ZipFile.OpenRead(savedPath))
            {
                foreach (var e in savedZip.Entries) savedNames.Add(e.FullName);
            }

            var copied = new List<string>();
            using (var snap = ZipFile.OpenRead(snapshotPath))
            using (var saved = ZipFile.Open(savedPath, ZipArchiveMode.Update))
            {
                foreach (var entry in snap.Entries)
                {
                    if (savedNames.Contains(entry.FullName)) continue;
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue; // directory entry

                    var newEntry = saved.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    using var src = entry.Open();
                    using var dst = newEntry.Open();
                    src.CopyTo(dst);
                    copied.Add(entry.FullName);
                }
            }
            if (copied.Count > 0)
                Program.WriteLog($"Merged {copied.Count} carry-forward entries from snapshot: {string.Join(", ", copied)}");
        }

        /// <summary>
        /// Rewrite info.xml inside the saved .hdb to set PdtVersionString to the
        /// loaded PDT framework's actual file version.
        /// </summary>
        private static void PatchInfoXmlVersion(string hdbPath)
        {
            var pdtVersion = ReadLoadedPdtVersion();
            if (string.IsNullOrEmpty(pdtVersion))
            {
                Program.WriteLog("Could not determine real PDT version; info.xml left as-is");
                return;
            }

            using var zip = ZipFile.Open(hdbPath, ZipArchiveMode.Update);
            var entry = zip.GetEntry("info.xml");
            if (entry == null)
            {
                Program.WriteLog("info.xml entry missing from saved hdb — skipping version patch");
                return;
            }

            string content;
            using (var s = entry.Open())
            using (var reader = new StreamReader(s, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            var trimmed = content.TrimEnd('\0');
            var patched = Regex.Replace(
                trimmed,
                @"<PdtVersionString>[^<]*</PdtVersionString>",
                $"<PdtVersionString>{pdtVersion}</PdtVersionString>");

            if (patched == trimmed)
            {
                Program.WriteLog("info.xml had no PdtVersionString tag — skipping version patch");
                return;
            }

            var newBytes = Encoding.UTF8.GetBytes(patched);
            entry.Delete();
            var newEntry = zip.CreateEntry("info.xml", CompressionLevel.Optimal);
            using var writer = newEntry.Open();
            writer.Write(newBytes, 0, newBytes.Length);

            Program.WriteLog($"Patched info.xml PdtVersionString → {pdtVersion}");
        }

        private static string ReadLoadedPdtVersion()
        {
            // Hydac.PDT.PdtFramework's file version (e.g. "2.12.102.19") is what PDT
            // considers the running PDT version — exactly what we want to report.
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Hydac.PDT.PdtFramework");
                if (asm != null && !string.IsNullOrEmpty(asm.Location))
                {
                    var fv = FileVersionInfo.GetVersionInfo(asm.Location);
                    if (!string.IsNullOrEmpty(fv.FileVersion)) return fv.FileVersion!;
                }

                // Fallback: try to find the DLL on disk in the working directory.
                var candidate = Path.Combine(Environment.CurrentDirectory, "Hydac.PDT.PdtFramework.dll");
                if (File.Exists(candidate))
                {
                    var fv = FileVersionInfo.GetVersionInfo(candidate);
                    if (!string.IsNullOrEmpty(fv.FileVersion)) return fv.FileVersion!;
                }
            }
            catch (Exception ex)
            {
                Program.WriteLog($"ReadLoadedPdtVersion failed: {ex.Message}");
            }
            return "2.12.102.19"; // last-resort fallback so we never report an empty version
        }

        /// <summary>
        /// Resolve a service via the public ServiceLocator using closed-generic reflection.
        /// Used for internal interfaces (ICliProjectCommands) and for arbitrary types we
        /// don't want to reference at compile time.
        /// </summary>
        public static object? ResolveByReflection(Type serviceType)
        {
            var resolveOpen = typeof(ServiceLocator)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Resolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            try
            {
                return resolveOpen.MakeGenericMethod(serviceType).Invoke(null, null);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }
        }

        /// <summary>Type-safe public resolve for types we reference directly.</summary>
        public T Resolve<T>() => ServiceLocator.Resolve<T>();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _cliInitializationType
                    .GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Static)?
                    .Invoke(null, null);
            }
            catch (Exception ex)
            {
                Program.WriteLog($"Dispose error (non-fatal): {ex.Message}");
            }
            try { if (File.Exists(PristineSnapshotPath)) File.Delete(PristineSnapshotPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// IApplicationVersionDetails replacement that reports the loaded PDT framework's
    /// file version as the current application version. PDT's DataLayerFactory uses this
    /// for its version-compatibility checks at load time and writes
    /// CurrentApplicationVersion into info.xml's PdtVersionString at save time. Without
    /// the override the helper's own AssemblyVersion is used — load rejects modern
    /// projects, and save stamps an out-of-range version that no future PDT can reload.
    ///
    /// LastSupportedPdt is deliberately old so CheckForUnsupportedProjectVersion always
    /// passes for any project this helper will see.
    /// </summary>
    internal sealed class HostApplicationVersionDetails : IApplicationVersionDetails
    {
        public HostApplicationVersionDetails(string pdtFileVersion)
        {
            CurrentApplicationVersion = TryParse(pdtFileVersion, fallback: new Version(2, 12, 102, 19));
        }

        public Version CurrentApplicationVersion { get; }
        public Version CodeBuilderVersion { get; } = new Version(1, 0, 0, 0);
        public Version CodeBuilderInterfaceVersion { get; } = new Version(1, 0, 0, 0);
        public Version LastSupportedPdt { get; } = new Version(2, 0, 0, 0);
        public Version LastSupportedMatch { get; } = new Version(1, 0, 0, 0);
        public string LastSupportedRelease { get; } = "2.0";
        public string NewestUnrestrictedPdt { get; } = "99.99.99";
        public Version MultiCoreMatchMinimumVersion { get; } = new Version(10, 0, 0, 0);

        private static Version TryParse(string s, Version fallback)
            => Version.TryParse(s, out var v) ? v : fallback;
    }
}
