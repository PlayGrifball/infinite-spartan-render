using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public sealed record JobResponse(string Id, string State, string Output);

public sealed record JobStatusResponse(string Id, string State, string? Error);

public sealed record ErrorResponse(string Error);

// Ready when a render could start: Blender is there, the Ekur data is mounted and the cache is writable.
public sealed class ReadinessCheck(IOptions<RenderOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (!File.Exists(o.BlenderPath)) return Task.FromResult(HealthCheckResult.Unhealthy("blender missing"));
        if (!File.Exists(Path.Combine(o.DataDirectory, "customization_globals.json"))) return Task.FromResult(HealthCheckResult.Unhealthy("data missing"));
        try
        {
            Directory.CreateDirectory(o.CacheDirectory);
            var probe = Path.Combine(o.CacheDirectory, ".ready");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("cache not writable"));
        }
        return Task.FromResult(HealthCheckResult.Healthy());
    }
}

public static class ForwardedHeadersSetup
{
    // Trusted proxies come from the ForwardedHeaders section; unparsable entries are ignored.
    public static void Apply(ForwardedHeadersOptions options, IConfiguration section)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var network in section.GetSection("KnownIPNetworks").Get<string[]>() ?? [])
            if (IPNetwork.TryParse(network, out var parsed)) options.KnownIPNetworks.Add(parsed);
        foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
            if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
        if (section["ForwardedForHeaderName"] is { Length: > 0 } header) options.ForwardedForHeaderName = header;
        if (int.TryParse(section["ForwardLimit"], out var limit) && limit > 0) options.ForwardLimit = limit;
    }
}
