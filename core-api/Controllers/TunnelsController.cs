using System.Security.Cryptography;
using core_api.Data;
using core_api.Models;
using core_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace core_api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TunnelsController(
    AppDbContext db,
    IOptions<TunnelingOptions> tunnelingOptions,
    INginxTunnelConfigWriter nginxTunnelConfig,
    ILogger<TunnelsController> log) : ControllerBase
{
    private readonly TunnelingOptions _tunneling = tunnelingOptions.Value;

    /// <summary>Allocates a unique subdomain for a local port and persists it.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(RegisterTunnelResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RegisterTunnelResponse>> Register([FromBody] RegisterTunnelRequest request, CancellationToken ct)
    {
        if (request.LocalPort is < 1 or > 65535)
            return BadRequest("localPort must be between 1 and 65535.");

        const int maxAttempts = 8;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var subdomain = NewSubdomainLabel();
            var row = new TunnelMapping
            {
                Subdomain = subdomain,
                LocalPort = request.LocalPort,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            db.TunnelMappings.Add(row);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                log.LogDebug(ex, "Subdomain collision, retrying.");
                db.Entry(row).State = EntityState.Detached;
                continue;
            }

            try
            {
                await nginxTunnelConfig.WriteTunnelServerBlockAsync(subdomain, request.LocalPort, _tunneling.BaseDomain, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to write nginx config for subdomain {Subdomain}; tunnel row is still saved.", subdomain);
            }

            var response = new RegisterTunnelResponse(
                Subdomain: subdomain,
                LocalPort: request.LocalPort,
                WebSocketServerUrl: "ws://localhost:4001",
                PublicHost: BuildPublicHost(subdomain));

            return StatusCode(StatusCodes.Status201Created, response);
        }

        return Problem(
            detail: "Could not allocate a unique subdomain after several attempts.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private string? BuildPublicHost(string subdomain)
    {
        if (string.IsNullOrWhiteSpace(_tunneling.BaseDomain))
            return null;
        return $"{subdomain}.{_tunneling.BaseDomain.Trim()}";
    }

    private static string NewSubdomainLabel()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public record RegisterTunnelRequest(int LocalPort);

public record RegisterTunnelResponse(
    string Subdomain,
    int LocalPort,
    string WebSocketServerUrl,
    string? PublicHost);
