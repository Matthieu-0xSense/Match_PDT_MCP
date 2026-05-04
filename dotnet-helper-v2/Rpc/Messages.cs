using System.Text.Json.Serialization;

namespace MatchPdt.Helper.Rpc
{
    internal sealed class RpcRequest
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")]      public int Id { get; set; }
        [JsonPropertyName("method")]  public string Method { get; set; } = "";
        [JsonPropertyName("params")]  public System.Text.Json.JsonElement? Params { get; set; }

        public T? GetParam<T>(string name) where T : struct
            => (Params is { } p && p.ValueKind == System.Text.Json.JsonValueKind.Object && p.TryGetProperty(name, out var v))
                ? System.Text.Json.JsonSerializer.Deserialize<T?>(v.GetRawText())
                : null;

        public string GetStringParam(string name, string fallback = "")
            => (Params is { } p && p.ValueKind == System.Text.Json.JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                ? (v.GetString() ?? fallback)
                : fallback;

        public int GetIntParam(string name, int fallback = 0)
            => (Params is { } p && p.ValueKind == System.Text.Json.JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                ? v.GetInt32()
                : fallback;
    }

    internal sealed class RpcResponse
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")]      public int Id { get; set; }
        [JsonPropertyName("result")]  public object? Result { get; set; }
        [JsonPropertyName("error")]   public RpcError? Error { get; set; }

        public static RpcResponse Ok(int id, object? result)
            => new RpcResponse { Id = id, Result = result };

        public static RpcResponse Fail(int id, int code, string message)
            => new RpcResponse { Id = id, Error = new RpcError { Code = code, Message = message } };
    }

    internal sealed class RpcError
    {
        [JsonPropertyName("code")]    public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    internal static class RpcErrorCodes
    {
        public const int ParseError     = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InternalError  = -32603;
    }
}
