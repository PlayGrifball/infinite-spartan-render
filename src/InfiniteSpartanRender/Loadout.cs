using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfiniteSpartanRender;

public sealed record Loadout
{
    public string Core { get; init; } = "";
    // Region (a collection name such as "helmet") to the permutation's display name; a region left out gets the core's default.
    public Dictionary<string, string>? Armor { get; init; } = new();
    // A coating id from the core's stylelists; empty means the default coating.
    public string? Coating { get; init; }
    public string Output { get; init; } = "png";
    public string Size { get; init; } = "small";

    private static readonly Regex CoatingId = new(@"^-?\d{1,12}$", RegexOptions.Compiled);

    public string? Validate(IReadOnlyCollection<string> cores)
    {
        if (string.IsNullOrWhiteSpace(Core) || Core.Length > 64) return "core is required (at most 64 characters)";
        if (!cores.Contains(Core)) return $"core must be one of: {string.Join(", ", cores)}";
        if (Armor is null) return "armor must be an object";
        if (Armor.Count > 64) return "armor has at most 64 entries";
        foreach (var (region, permutation) in Armor)
        {
            if (region is null || permutation is null || region.Length is 0 or > 64 || permutation.Length is 0 or > 128)
                return "armor keys are 1-64 and values 1-128 characters";
        }
        if (!string.IsNullOrEmpty(Coating) && !CoatingId.IsMatch(Coating)) return "coating must be a numeric coating id";
        if (Output is not ("png" or "glb")) return "output must be png or glb";
        if (Size is not ("small" or "full")) return "size must be small or full";
        return null;
    }

    // The same loadout always maps to the same id, whatever order the armor was sent in.
    public string Id()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            core = Core,
            armor = (Armor ?? new()).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new[] { a.Key, a.Value }),
            coating = string.IsNullOrEmpty(Coating) ? "" : Coating,
            output = Output,
            size = Size,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32].ToLowerInvariant();
    }
}
