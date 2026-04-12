namespace core_api;

public class TunnelingOptions
{
    public const string SectionName = "Tunneling";

    /// <summary>Public WebSocket URL clients use to open the tunnel session (e.g. wss://ws.tunnel.example.com).</summary>
    public string WebSocketServerUrl { get; set; } = "ws://localhost:8081";

    /// <summary>Optional suffix for building a public host from the subdomain (e.g. "tunnel.example.com").</summary>
    public string? BaseDomain { get; set; }

    /// <summary>
    /// Directory where per-tunnel nginx snippets are written (one <c>server</c> block per file).
    /// If relative, it is resolved under the API content root. Leave empty to disable file generation.
    /// </summary>
    public string? NginxTunnelConfDirectory { get; set; }

    /// <summary>
    /// Upstream URL template for <c>proxy_pass</c> (path is appended automatically). Placeholders: <c>{LocalPort}</c>, <c>{Subdomain}</c>.
    /// Typical Docker Desktop: <c>http://host.docker.internal:{LocalPort}</c>.
    /// </summary>
    public string NginxProxyPassTemplate { get; set; } = "http://host.docker.internal:{LocalPort}";
}
