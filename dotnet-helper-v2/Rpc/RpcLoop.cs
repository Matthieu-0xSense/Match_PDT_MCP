using System;
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
                    "ping"     => RpcResponse.Ok(req.Id, "pong"),
                    "shutdown" => RpcResponse.Ok(req.Id, "ok"),

                    // Phase 2+ handlers plug in here:
                    // "add_custom_error" => ErrorService.AddCustomError(_host, req),
                    // "add_can_message"  => CanMessageService.Add(_host, req),

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
