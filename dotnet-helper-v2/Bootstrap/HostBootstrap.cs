using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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

        private readonly Type _cliInitializationType;
        private bool _disposed;

        private HostBootstrap(Type cliInitializationType, object project, string smokeTestServiceTypeName)
        {
            _cliInitializationType = cliInitializationType;
            Project = project;
            SmokeTestServiceTypeName = smokeTestServiceTypeName;
        }

        public static HostBootstrap Initialize(FileInfo hdbFile)
        {
            var sw = Stopwatch.StartNew();

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

            // 1. Run the canonical bootstrap. CliSetup is `internal class`, has a default ctor,
            //    and CreateApplication() is the public-on-the-type-but-internal-to-the-assembly
            //    method that does the whole dance.
            Program.WriteLog("Calling CliSetup.CreateApplication...");
            var setup = Activator.CreateInstance(
                cliSetupType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: null, culture: null)
                ?? throw new InvalidOperationException("CliSetup ctor returned null");
            var createApp = cliSetupType.GetMethod(
                "CreateApplication",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("CliSetup.CreateApplication");
            try
            {
                createApp.Invoke(setup, null);
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

            return new HostBootstrap(
                cliInitType,
                project,
                messageService.GetType().FullName ?? "<unknown>");
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
        }
    }
}
