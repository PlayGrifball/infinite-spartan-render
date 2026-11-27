using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

// One consumer of the API, by name: the service's telemetry (and, through the trace, the Grif Telemetry page's
// gate) tells callers apart by it, so it is the name of a key the caller proved it holds, never a claim.
public sealed class CallerKey
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class CallerKeys(IOptions<RenderOptions> options)
{
    public const string Header = "X-Api-Key";

    // The span tag the Telemetry gate reads a downstream service's caller from (the Halo gateway sets the same).
    public const string SpanTag = "grunt.api_key";

    // The name of the key presented, or null for none that matches. Every key is compared, in fixed time.
    public string? Match(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        string? found = null;
        foreach (var key in options.Value.ApiKeys)
            if (key.Key.Length > 0 && !string.IsNullOrWhiteSpace(key.Name) && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(key.Key))))
                found ??= key.Name;
        return found;
    }
}
