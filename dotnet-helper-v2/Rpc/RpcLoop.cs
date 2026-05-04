using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using MatchPdt.Helper.Bootstrap;

namespace MatchPdt.Helper.Rpc
{
    /// <summary>
    /// Reads JSON-RPC requests from stdin (one per line), marshals them onto the
    /// dispatcher thread for execution, writes responses to stdout. Logging goes to
    /// stderr — stdout is reserved for JSON.
    /// </summary>
    internal sealed class RpcLoop
    {
        private readonly Dispatcher _dispatcher;
        private readonly HostBootstrap _host;

        public RpcLoop(Dispatcher dispatcher, HostBootstrap host)
        {
            _dispatcher = dispatcher;
            _host = host;
        }

        public async Task RunAsync()
        {
            var reader = Console.In;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;

                RpcRequest? req;
                try
                {
                    req = JsonSerializer.Deserialize<RpcRequest>(line);
                }
                catch (JsonException ex)
                {
                    WriteResponse(RpcResponse.Fail(0, RpcErrorCodes.ParseError, ex.Message));
                    continue;
                }
                if (req is null)
                {
                    WriteResponse(RpcResponse.Fail(0, RpcErrorCodes.InvalidRequest, "null request"));
                    continue;
                }

                var response = await _dispatcher.InvokeAsync(() => Dispatch(req)).Task.ConfigureAwait(false);
                WriteResponse(response);

                if (req.Method == "shutdown") break;
            }

            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }

        // Runs on the dispatcher thread.
        private RpcResponse Dispatch(RpcRequest req)
        {
            try
            {
                return req.Method switch
                {
                    "ping"             => RpcResponse.Ok(req.Id, "pong"),
                    "shutdown"         => RpcResponse.Ok(req.Id, "ok"),
                    "save_project"     => SaveProject(req),
                    "add_custom_error" => Services.ErrorService.AddCustomError(_host, req),
                    "delete_err_block" => Services.ErrorService.DeleteErrBlock(_host, req),
                    "add_can_message"  => Services.CanMessageService.AddCanMessage(_host, req),
                    "probe_type"       => ProbeType(req),
                    "dump_block_types" => RpcResponse.Ok(req.Id, Services.ErrorService.DumpStandardBlockTypes()),

                    _ => RpcResponse.Fail(req.Id, RpcErrorCodes.MethodNotFound, req.Method),
                };
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(
                    req.Id,
                    RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private RpcResponse SaveProject(RpcRequest req)
        {
            _host.SaveProject();
            return RpcResponse.Ok(req.Id, new { saved = _host.HdbFile.FullName });
        }

        // Diagnostic: resolve any IFoo via ServiceLocator and report the concrete type +
        // its assembly + ctor dependencies. Lets us inspect bindings that dnSpy class
        // browsing can't easily surface. Params: { "interface": "Hydac.PDT...IFoo",
        // "assembly": "Hydac.PDT.Business.Interfaces" }
        private RpcResponse ProbeType(RpcRequest req)
        {
            var iface = req.GetStringParam("interface");
            var assembly = req.GetStringParam("assembly");
            if (string.IsNullOrEmpty(iface) || string.IsNullOrEmpty(assembly))
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InvalidRequest,
                    "probe_type needs {interface, assembly}");

            try
            {
                var asm = System.Reflection.Assembly.Load(assembly);
                var t = asm.GetType(iface, throwOnError: true)!;
                var instance = Bootstrap.HostBootstrap.ResolveByReflection(t);
                if (instance == null)
                    return RpcResponse.Ok(req.Id, new { resolved = false, reason = "ServiceLocator returned null" });

                var concrete = instance.GetType();
                var ctors = concrete.GetConstructors(System.Reflection.BindingFlags.Public |
                                                    System.Reflection.BindingFlags.NonPublic |
                                                    System.Reflection.BindingFlags.Instance);
                var methods = concrete.GetMethods(System.Reflection.BindingFlags.Public |
                                                  System.Reflection.BindingFlags.NonPublic |
                                                  System.Reflection.BindingFlags.Instance |
                                                  System.Reflection.BindingFlags.DeclaredOnly)
                    .Select(m => $"{(m.IsPublic ? "public" : "internal")} {m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})")
                    .Take(50)
                    .ToList();
                return RpcResponse.Ok(req.Id, new
                {
                    resolved = true,
                    concrete_type = concrete.FullName,
                    assembly = concrete.Assembly.GetName().Name,
                    ctors = ctors.Select(c => string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))).ToList(),
                    methods,
                });
            }
            catch (Exception ex)
            {
                return RpcResponse.Fail(req.Id, RpcErrorCodes.InternalError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void WriteResponse(RpcResponse resp)
        {
            var json = JsonSerializer.Serialize(resp, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
    }
}
