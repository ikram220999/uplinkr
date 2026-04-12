namespace core_api.Services;

/// <summary>Writes per-tunnel nginx <c>server</c> snippets so HTTP traffic can be forwarded to the registered upstream.</summary>
public interface INginxTunnelConfigWriter
{
    /// <summary>
    /// Writes <c>tunnel-{subdomain}.conf</c> under the configured output directory (if set).
    /// <paramref name="baseDomain"/> is used to build <c>server_name</c> (e.g. <c>sub</c> and <c>sub.example.com</c>).
    /// </summary>
    Task WriteTunnelServerBlockAsync(string subdomain, int localPort, string? baseDomain, CancellationToken cancellationToken = default);
}
