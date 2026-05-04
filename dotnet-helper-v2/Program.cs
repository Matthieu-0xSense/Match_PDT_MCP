using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using MatchPdt.Helper.Bootstrap;
using MatchPdt.Helper.Rpc;

namespace MatchPdt.Helper
{
    internal static class Program
    {
        // STAThread is required: CliInitialization.AddMessageBoxProvider captures
        // Dispatcher.CurrentDispatcher and stores it in TObjects, and
        // CliProjectCommands.LoadProjectAsync delegates to TMainVM.LoadProjectCommand —
        // both assume the WPF dispatcher.
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                WriteLog("usage: Match_PDT_Helper_v2 <path-to-hdb>");
                return 2;
            }

            var hdbPath = args[0];
            if (!File.Exists(hdbPath))
            {
                WriteLog($"hdb not found: {hdbPath}");
                return 2;
            }

            // PDT lives outside the helper's bin dir, so default fusion probing doesn't
            // find it. WorkingDirectory is set to the PDT install dir by the launcher;
            // resolve any unfound assembly from there.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromWorkingDir;

            // Touch CurrentDispatcher to materialise it on this STA thread *before* we
            // run bootstrap. CliInitialization.AddMessageBoxProvider stores it in TObjects
            // — fine to capture before Dispatcher.Run() starts.
            var dispatcher = Dispatcher.CurrentDispatcher;
            WriteLog("STA dispatcher captured");

            try
            {
                // Bootstrap runs synchronously on the STA thread *without* the dispatcher
                // pump active — same as the PDT CLI. Running bootstrap inside a
                // BeginInvoke would cause sync-over-async deadlocks: PDT's startup awaits
                // (KnowledgeBaseAgentInitialization, LoadProjectCommand) and the
                // continuations capture the dispatcher's SynchronizationContext; if we're
                // *already* blocking the dispatcher with a synchronous callback, those
                // continuations queue up forever.
                var host = HostBootstrap.Initialize(new FileInfo(hdbPath));
                WriteLog($"Smoke test: Resolved {host.SmokeTestServiceTypeName}");

                Console.Out.WriteLine("Ready.");
                Console.Out.Flush();

                // Now start the dispatcher pump and let the RPC loop marshal each
                // incoming request onto it. Stdio reads happen on a background thread.
                var rpc = new RpcLoop(dispatcher, host);
                Task.Run(rpc.RunAsync);

                Dispatcher.Run();
                WriteLog("Dispatcher exited");
                return 0;
            }
            catch (Exception ex)
            {
                WriteLog($"FATAL: {ex.GetType().Name}: {ex.Message}");
                WriteLog(ex.ToString());
                return 1;
            }
        }

        // stderr only — stdout is reserved for JSON-RPC.
        internal static void WriteLog(string line)
        {
            Console.Error.WriteLine($"[helper] {line}");
            Console.Error.Flush();
        }

        private static Assembly? ResolveFromWorkingDir(object? sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simpleName)) return null;

            var workingDir = Environment.CurrentDirectory;
            var candidates = new[]
            {
                Path.Combine(workingDir, simpleName + ".dll"),
                Path.Combine(workingDir, simpleName + ".exe"),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    try { return Assembly.LoadFrom(path); }
                    catch (Exception ex) { WriteLog($"AssemblyResolve {simpleName}: LoadFrom failed: {ex.Message}"); }
                }
            }
            return null;
        }
    }
}
