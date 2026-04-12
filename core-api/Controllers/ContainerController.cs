using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Mvc;

namespace core_api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContainerController(IDockerClient docker, ILogger<ContainerController> log) : ControllerBase
{
    public const string ScaleGroupLabelKey = "core-api.scale-group";

    /// <summary>Create and start a container from an image with optional parameters.</summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(RunContainerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RunContainerResponse>> Run([FromBody] RunContainerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Image))
            return BadRequest("image is required.");

        var labels = MergeLabels(request.Labels, scaleGroup: null);
        var create = new CreateContainerParameters
        {
            Image = request.Image.Trim(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            Env = ToEnvList(request.Environment),
            Cmd = request.Cmd?.ToList(),
            Entrypoint = request.Entrypoint?.ToList(),
            HostConfig = BuildHostConfig(request),
            Labels = labels.Count > 0 ? labels : null
        };
        ApplyExposedPorts(create, request);

        CreateContainerResponse created;
        try
        {
            created = await docker.Containers.CreateContainerAsync(create, ct);
        }
        catch (DockerApiException ex)
        {
            log.LogWarning(ex, "Docker create failed for image {Image}", request.Image);
            return Problem(detail: ex.Message, statusCode: (int)ex.StatusCode);
        }

        try
        {
            await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        }
        catch (DockerApiException ex)
        {
            log.LogError(ex, "Docker start failed for container {Id}", created.ID);
            return Problem(detail: ex.Message, statusCode: (int)ex.StatusCode);
        }

        return StatusCode(StatusCodes.Status201Created,
            new RunContainerResponse(created.ID, created.Warnings));
    }

    /// <summary>Stop (if needed) and remove a container by id.</summary>
    [HttpDelete("{containerId}")]
    [ProducesResponseType(typeof(RemoveContainerResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RemoveContainerResponse>> Remove(string containerId, [FromQuery] bool force = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            return BadRequest("containerId is required.");

        try
        {
            await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = force }, ct);
        }
        catch (DockerApiException ex)
        {
            log.LogWarning(ex, "Docker remove failed for {Id}", containerId);
            return Problem(detail: ex.Message, statusCode: (int)ex.StatusCode);
        }

        return Ok(new RemoveContainerResponse(containerId.Trim(), Removed: true));
    }

    /// <summary>
    /// Scale a logical group of containers: adds or removes replicas tagged with <see cref="ScaleGroupLabelKey"/>.
    /// New containers are named <c>{scaleGroup}-{n}</c> when <paramref name="request.NamePrefix"/> is omitted.
    /// </summary>
    [HttpPost("scale")]
    [ProducesResponseType(typeof(ScaleContainersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ScaleContainersResponse>> Scale([FromBody] ScaleContainersRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Image))
            return BadRequest("image is required.");
        if (string.IsNullOrWhiteSpace(request.ScaleGroup))
            return BadRequest("scaleGroup is required.");
        if (request.Replicas < 0)
            return BadRequest("replicas must be zero or positive.");

        var group = request.ScaleGroup.Trim();
        var image = request.Image.Trim();

        var list = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{ScaleGroupLabelKey}={group}"] = true }
            }
        }, ct);

        var existing = list
            .Where(c => c.Labels != null && c.Labels.TryGetValue(ScaleGroupLabelKey, out var v) && v == group)
            .OrderByDescending(c => c.Created)
            .ToList();

        var current = existing.Count;
        var target = request.Replicas;
        var touched = new List<string>();

        if (target > current)
        {
            var prefix = string.IsNullOrWhiteSpace(request.NamePrefix) ? SanitizeNamePrefix(group) : request.NamePrefix!.Trim();
            for (var i = 0; i < target - current; i++)
            {
                var suffix = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant();
                var name = $"{prefix}-{suffix}";

                var labels = MergeLabels(request.Labels, group);
                var create = new CreateContainerParameters
                {
                    Image = image,
                    Name = name,
                    Env = ToEnvList(request.Environment),
                    Cmd = request.Cmd?.ToList(),
                    Entrypoint = request.Entrypoint?.ToList(),
                    HostConfig = BuildHostConfig(request),
                    Labels = labels
                };
                ApplyExposedPorts(create, request);

                CreateContainerResponse created;
                try
                {
                    created = await docker.Containers.CreateContainerAsync(create, ct);
                }
                catch (DockerApiException ex)
                {
                    log.LogWarning(ex, "Scale-up create failed for group {Group}", group);
                    return Problem(detail: ex.Message, statusCode: (int)ex.StatusCode);
                }

                try
                {
                    await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
                }
                catch (DockerApiException ex)
                {
                    log.LogError(ex, "Scale-up start failed for {Id}", created.ID);
                    return Problem(detail: ex.Message, statusCode: (int)ex.StatusCode);
                }

                touched.Add(created.ID);
            }
        }
        else if (target < current)
        {
            var removeCount = current - target;
            foreach (var c in existing.Take(removeCount))
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true }, ct);
                }
                catch (DockerApiException ex)
                {
                    log.LogWarning(ex, "Scale-down remove failed for {Id}", c.ID);
                    return Problem(detail: ex.Message, statusCode: (int)ex.StatusCode);
                }

                touched.Add(c.ID);
            }
        }

        var after = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{ScaleGroupLabelKey}={group}"] = true }
            }
        }, ct);

        var ids = after
            .Where(c => c.Labels != null && c.Labels.TryGetValue(ScaleGroupLabelKey, out var v) && v == group)
            .Select(c => c.ID)
            .ToList();

        return Ok(new ScaleContainersResponse(group, image, target, ids, touched));
    }

    private static void ApplyExposedPorts(CreateContainerParameters create, RunContainerRequestBase request)
    {
        var ports = request.Host?.PortBindings;
        if (ports is not { Count: > 0 })
            return;

        create.ExposedPorts = new Dictionary<string, EmptyStruct>();
        foreach (var pb in ports)
        {
            var cp = pb.ContainerPort.Trim();
            if (cp.Length == 0)
                continue;
            if (!cp.Contains('/'))
                cp += "/tcp";
            create.ExposedPorts[cp] = default;
        }
    }

    private static HostConfig BuildHostConfig(RunContainerRequestBase request)
    {
        var hc = request.Host ?? new HostConfigRequest();
        var hostConfig = new HostConfig
        {
            PublishAllPorts = hc.PublishAllPorts,
            NetworkMode = string.IsNullOrWhiteSpace(hc.NetworkMode) ? null : hc.NetworkMode,
            AutoRemove = hc.AutoRemove
        };

        if (hc.PortBindings is { Count: > 0 })
        {
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>();
            foreach (var pb in hc.PortBindings)
            {
                var containerPort = pb.ContainerPort;
                if (string.IsNullOrWhiteSpace(containerPort))
                    continue;
                if (!containerPort.Contains('/'))
                    containerPort += "/tcp";

                hostConfig.PortBindings[containerPort] = new List<PortBinding>
                {
                    new()
                    {
                        HostPort = string.IsNullOrWhiteSpace(pb.HostPort) ? null : pb.HostPort,
                        HostIP = string.IsNullOrWhiteSpace(pb.HostIp) ? "0.0.0.0" : pb.HostIp
                    }
                };
            }
        }

        return hostConfig;
    }

    private static Dictionary<string, string> MergeLabels(IDictionary<string, string>? extra, string? scaleGroup)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extra != null)
        {
            foreach (var kv in extra)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    d[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        if (scaleGroup != null)
            d[ScaleGroupLabelKey] = scaleGroup;

        return d;
    }

    private static IList<string>? ToEnvList(IReadOnlyDictionary<string, string>? env)
    {
        if (env == null || env.Count == 0)
            return null;
        return env.Select(kv => $"{kv.Key}={kv.Value}").ToList();
    }

    private static string SanitizeNamePrefix(string scaleGroup)
    {
        var chars = scaleGroup.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray();
        var s = new string(chars);
        return string.IsNullOrEmpty(s) ? "scaled" : s.ToLowerInvariant();
    }
}

public abstract class RunContainerRequestBase
{
    public HostConfigRequest? Host { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<string>? Cmd { get; set; }
    public List<string>? Entrypoint { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

public class RunContainerRequest : RunContainerRequestBase
{
    public string Image { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public class ScaleContainersRequest : RunContainerRequestBase
{
    public string Image { get; set; } = string.Empty;
    public string ScaleGroup { get; set; } = string.Empty;
    public int Replicas { get; set; }
    public string? NamePrefix { get; set; }
}

public class HostConfigRequest
{
    public bool PublishAllPorts { get; set; }
    public string? NetworkMode { get; set; }
    public bool AutoRemove { get; set; }
    public List<PortBindingRequest>? PortBindings { get; set; }
}

public class PortBindingRequest
{
    public string ContainerPort { get; set; } = string.Empty;
    public string? HostPort { get; set; }
    public string? HostIp { get; set; }
}

public record RunContainerResponse(string ContainerId, IList<string>? Warnings);

public record RemoveContainerResponse(string ContainerId, bool Removed);

public record ScaleContainersResponse(
    string ScaleGroup,
    string Image,
    int Replicas,
    IReadOnlyList<string> ContainerIds,
    IReadOnlyList<string> TouchedContainerIds);
