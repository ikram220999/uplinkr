namespace WebSocketBridge;

// Message shapes:
// - request:  { type:"request",  id:"...", request:{ method, path, headers, body, isBase64? } }
// - response: { type:"response", id:"...", response:{ status, headers?, body, isBase64? } }

internal sealed class BridgeEnvelope
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public BridgeRequest? Request { get; set; }
    public BridgeResponse? Response { get; set; }
}

internal sealed class BridgeRequest
{
    public string? Method { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    public bool? IsBase64 { get; set; }
}

internal sealed class BridgeResponse
{
    public int Status { get; set; } = 200;
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    public bool IsBase64 { get; set; }
}
