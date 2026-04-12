namespace core_api.Models;

public class TunnelMapping
{
    public int Id { get; set; }

    /// <summary>DNS label used as the tunnel subdomain (e.g. "a1b2c3d4e5f67890").</summary>
    public string Subdomain { get; set; } = string.Empty;

    public int LocalPort { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
