using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfiniteSpartanRender;

// A piece addressed by the game's ids, for pieces that have no name (some kits).
public sealed record PartId(int Region, int Permutation);

public sealed record Loadout
{
    public string Core { get; init; } = "";
    // Region (a collection name such as "helmet") to the permutation's display name; a region left out gets the core's default.
    public Dictionary<string, string>? Armor { get; init; } = new();
    // Pieces by (region id, permutation id), as in the game's customization data. A part replaces whatever `armor` or the
    // default would give for the region it sits in, and keeps all of that permutation's meshes.
    public List<PartId>? Parts { get; init; } = new();
    // A coating id from the core's stylelists; empty means the default coating.
    public string? Coating { get; init; }
    // A visor title from the catalog; empty means the core's default visor.
    public string? Visor { get; init; }
    // Ekur's Disable Damage: leaves out the 7th or 4th material zone, the layer of grime and wear.
    public bool DisableDamage { get; init; }
    public string Output { get; init; } = "png";
    public string Size { get; init; } = "small";
    // body | head: the head frames the helmet only.
    public string Framing { get; init; } = "body";
    // A StanceId from a player's action pose item; empty means the default stance.
    public int? Stance { get; init; }
    // A weapon name from the catalog, held in the right hand; empty means empty hands.
    public string? Weapon { get; init; }
    // A coating id from the weapon's stylelists; empty means the weapon's default coating.
    public string? WeaponCoating { get; init; }

    private static readonly Regex CoatingId = new(@"^-?\d{1,12}$", RegexOptions.Compiled);

    public string? Validate(IReadOnlyCollection<string> cores, IReadOnlyCollection<string>? visors = null, IReadOnlyCollection<string>? weapons = null)
    {
        if (string.IsNullOrWhiteSpace(Core) || Core.Length > 64) return "core is required (at most 64 characters)";
        if (!cores.Contains(Core)) return $"core must be one of: {string.Join(", ", cores)}";
        if (PiecesProblem() is { } pieces) return pieces;
        if (!string.IsNullOrEmpty(Coating) && !CoatingId.IsMatch(Coating)) return "coating must be a numeric coating id";
        if (!string.IsNullOrEmpty(Visor) && visors is not null && !visors.Contains(Visor)) return "visor must be a title from the catalog";
        if (!string.IsNullOrEmpty(Weapon) && (Weapon.Length > 64 || (weapons is not null && !weapons.Contains(Weapon)))) return "weapon must be a name from the catalog";
        if (!string.IsNullOrEmpty(WeaponCoating) && !CoatingId.IsMatch(WeaponCoating)) return "weapon coating must be a numeric coating id";
        if (!string.IsNullOrEmpty(WeaponCoating) && string.IsNullOrEmpty(Weapon)) return "weapon coating needs a weapon";
        if (Output is not ("png" or "glb")) return "output must be png or glb";
        if (Size is not ("small" or "full")) return "size must be small or full";
        if (Framing is not ("body" or "head")) return "framing must be body or head";
        return null;
    }

    private string? PiecesProblem()
    {
        if (Armor is null) return "armor must be an object";
        if (Armor.Count > 64) return "armor has at most 64 entries";
        foreach (var (region, permutation) in Armor)
        {
            if (region is null || permutation is null || region.Length is 0 or > 64 || permutation.Length is 0 or > 128)
                return "armor keys are 1-64 and values 1-128 characters";
        }
        if (Parts is null) return "parts must be an array";
        if (Parts.Count > 64) return "parts has at most 64 entries";
        if (Parts.Any(p => p is null)) return "parts entries must be objects with region and permutation";
        if (Parts.Select(p => p.Region).Distinct().Count() != Parts.Count) return "parts has one entry per region";
        return null;
    }

    // The same loadout always maps to the same id, whatever order the armor was sent in.
    // `renderer` names the renderer that made the file, so a changed renderer never serves files an older one made.
    public string Id(string renderer = "")
    {
        var canonical = JsonSerializer.Serialize(new
        {
            renderer,
            core = Core,
            armor = (Armor ?? new()).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new[] { a.Key, a.Value }),
            parts = (Parts ?? new()).OrderBy(p => p.Region).Select(p => new[] { p.Region, p.Permutation }),
            coating = string.IsNullOrEmpty(Coating) ? "" : Coating,
            visor = string.IsNullOrEmpty(Visor) ? "" : Visor,
            disableDamage = DisableDamage,
            output = Output,
            size = Size,
            framing = Framing,
            stance = Stance,
            weapon = string.IsNullOrEmpty(Weapon) ? "" : Weapon,
            weaponCoating = string.IsNullOrEmpty(WeaponCoating) ? "" : WeaponCoating,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32].ToLowerInvariant();
    }
}
