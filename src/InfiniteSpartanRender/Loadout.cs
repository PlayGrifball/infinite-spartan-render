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
    // More clips for a model: StanceIds from the catalog's poses, each clip named after its pose and holding its weapon (output glb only).
    public List<int>? ExtraPoses { get; init; }
    // Weapon name to coating id, for the weapons the extra poses hold other than Weapon (which wears WeaponCoating).
    public Dictionary<string, string>? WeaponCoatings { get; init; }

    private static readonly Regex CoatingId = new(@"^-?\d{1,12}$", RegexOptions.Compiled);

    public bool HasExtraPoses => ExtraPoses is { Count: > 0 };

    public string? Validate(IReadOnlyCollection<string> cores, IReadOnlyCollection<string>? visors = null, IReadOnlyCollection<CatalogWeapon>? weapons = null,
        IReadOnlyCollection<CatalogPose>? poses = null)
    {
        if (string.IsNullOrWhiteSpace(Core) || Core.Length > 64) return "core is required (at most 64 characters)";
        if (!cores.Contains(Core)) return $"core must be one of: {string.Join(", ", cores)}";
        if (PiecesProblem() is { } pieces) return pieces;
        if (!string.IsNullOrEmpty(Coating) && !CoatingId.IsMatch(Coating)) return "coating must be a numeric coating id";
        if (!string.IsNullOrEmpty(Visor) && visors is not null && !visors.Contains(Visor)) return "visor must be a title from the catalog";
        if (WeaponProblem(weapons) is { } weapon) return weapon;
        return ShapeProblem() ?? PosesProblem(poses) ?? ModelProblem(weapons);
    }

    private string? ShapeProblem()
    {
        if (Output is not ("png" or "glb")) return "output must be png or glb";
        if (Size is not ("small" or "full")) return "size must be small or full";
        return Framing is not ("body" or "head") ? "framing must be body or head" : null;
    }

    private string? PosesProblem(IReadOnlyCollection<CatalogPose>? poses)
    {
        if (!HasExtraPoses) return null;
        if (Output != "glb") return "extra poses need output glb";
        if (ExtraPoses!.Count > 128) return "extra poses has at most 128 entries";
        return poses is not null && ExtraPoses.Any(s => poses.All(p => p.Stance != s)) ? "extra poses must be stances from the catalog's poses" : null;
    }

    private string? WeaponProblem(IReadOnlyCollection<CatalogWeapon>? weapons)
    {
        var weapon = weapons?.FirstOrDefault(w => w.Name == Weapon);
        if (!string.IsNullOrEmpty(Weapon) && (Weapon.Length > 64 || (weapons is not null && weapon is null))) return "weapon must be a name from the catalog";
        if (string.IsNullOrEmpty(WeaponCoating)) return null;
        if (!CoatingId.IsMatch(WeaponCoating)) return "weapon coating must be a numeric coating id";
        if (string.IsNullOrEmpty(Weapon)) return "weapon coating needs a weapon";
        // Ekur silently renders the default for a coating the weapon's stylelists lack, which would cache a wrong render under this id
        return weapon is not null && !weapon.Coatings.Contains(WeaponCoating) ? "weapon coating must be one of that weapon's coatings" : null;
    }

    private string? ModelProblem(IReadOnlyCollection<CatalogWeapon>? weapons)
    {
        if (WeaponCoatings is null || weapons is null) return null;
        if (WeaponCoatings.Count > 64) return "weapon coatings has at most 64 entries";
        foreach (var (name, coat) in WeaponCoatings)
        {
            if (weapons.FirstOrDefault(w => w.Name == name) is not { } weapon) return "weapon coatings name a weapon from the catalog";
            if (coat is null || !weapon.Coatings.Contains(coat)) return "weapon coatings are each one of that weapon's coatings";
        }
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
            extraPoses = (ExtraPoses ?? []).Distinct().Order(),
            weaponCoatings = (WeaponCoatings ?? new()).OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => new[] { w.Key, w.Value }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32].ToLowerInvariant();
    }
}
