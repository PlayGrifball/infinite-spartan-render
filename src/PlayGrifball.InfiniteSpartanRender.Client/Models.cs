namespace PlayGrifball.InfiniteSpartanRender.Client;

/// <summary>A piece by the game's ids: the model region id and the permutation id.</summary>
public sealed record SpartanPart(int Region, int Permutation);

/// <summary>What to render: a core, its armor pieces and coating, and how.</summary>
public sealed record SpartanLoadout
{
    /// <summary>The armor core, e.g. <c>Mark VII</c>.</summary>
    public required string Core { get; init; }

    /// <summary>Region (e.g. <c>helmet</c>) to permutation name; a region left out gets the core's default piece.</summary>
    public IDictionary<string, string> Armor { get; init; } = new Dictionary<string, string>();

    /// <summary>Pieces by region id and permutation id, for pieces with no name (some kits); one per region. They win over <see cref="Armor"/>.</summary>
    public IList<SpartanPart> Parts { get; init; } = new List<SpartanPart>();

    /// <summary>A coating id; null or empty is the default coating.</summary>
    public string? Coating { get; init; }

    /// <summary>A visor title from the catalog's <c>visors</c>; null or empty is the core's default visor.</summary>
    public string? Visor { get; init; }

    /// <summary>Leaves out the 7th or 4th material zone, the layer of grime and wear on armor, for a cleaner look.</summary>
    public bool DisableDamage { get; init; }

    /// <summary><c>png</c> or <c>glb</c>.</summary>
    public string Output { get; init; } = "png";

    /// <summary><c>small</c> or <c>full</c>.</summary>
    public string Size { get; init; } = "small";

    /// <summary><c>body</c> or <c>head</c> (the head frames the helmet).</summary>
    public string Framing { get; init; } = "body";

    /// <summary>A StanceId from a player's action pose item; null is the default relaxed stance.</summary>
    public int? Stance { get; init; }

    /// <summary>A weapon name from the catalog's <c>weapons</c>, held in the right hand; null or empty is empty hands.</summary>
    public string? Weapon { get; init; }

    /// <summary>A coating id from the weapon's <c>coatings</c>; null or empty is the weapon's default coating. Needs <see cref="Weapon"/>.</summary>
    public string? WeaponCoating { get; init; }
}

/// <summary>A render job: <see cref="State"/> is queued, running, done or failed.</summary>
public sealed record SpartanJob(string Id, string State, string? Output = null, string? Error = null)
{
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";
}

/// <summary>A Spartan in a chosen stance and in the neutral one: a GLB, a body PNG and a headshot of each.</summary>
public sealed record SpartanSetRequest
{
    public required string Core { get; init; }
    public IDictionary<string, string> Armor { get; init; } = new Dictionary<string, string>();
    public IList<SpartanPart> Parts { get; init; } = new List<SpartanPart>();
    public string? Coating { get; init; }
    /// <summary>A visor title from the catalog; null or empty is the core's default visor.</summary>
    public string? Visor { get; init; }
    /// <summary>Leaves out the grime and wear zone, for a cleaner look.</summary>
    public bool DisableDamage { get; init; }

    /// <summary>A StanceId from a player's action pose item (required by the API).</summary>
    public required int Stance { get; init; }

    /// <summary>The weapon the stance holds, from the catalog: in the stance renders and the GLB's stance clip, never in the neutral pose.</summary>
    public string? Weapon { get; init; }

    /// <summary>A coating id from the weapon's <c>coatings</c>; null or empty is its default coating.</summary>
    public string? WeaponCoating { get; init; }

    public string Size { get; init; } = "small";
}

/// <summary>One of the five files of a set: <see cref="Name"/> is glb, stance-body, stance-head, neutral-body or neutral-head.</summary>
public sealed record SpartanSetItem(string Name, string State, string Output, string Url);

/// <summary>A set's state is the worst of its five items: failed, else done when all are, else running or queued.</summary>
public sealed record SpartanSet(string Id, string State, IReadOnlyList<SpartanSetItem> Items)
{
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";
}

/// <summary>A piece of a region: <see cref="Name"/> goes in <see cref="SpartanLoadout.Armor"/>; with no name (some kits) use <see cref="SpartanPart"/> by region and permutation id.</summary>
public sealed record SpartanCatalogPiece(int Permutation, string? Name);

/// <summary>A region of a core. <see cref="Key"/> is the <see cref="SpartanLoadout.Armor"/> key; <see cref="Kind"/> is REGION, or KIT for a kit's own regions.</summary>
public sealed record SpartanCatalogRegion(string Kind, string Key, int Region, IReadOnlyList<SpartanCatalogPiece> Pieces);

public sealed record SpartanCatalogCore(string Name, IReadOnlyList<SpartanCatalogRegion> Regions);

/// <summary>
/// A weapon a stance can hold. <see cref="Tags"/> are the action pose <c>weaponTagId</c>s that mean it; <see cref="CoreType"/> is the Halo
/// weapon core (e.g. <c>WeaponCommando</c>) whose equipped theme gives its coating, null when the weapon has none; <see cref="Coatings"/> are its coating ids.
/// </summary>
public sealed record SpartanCatalogWeapon(string Name, string? CoreType, IReadOnlyList<int> Tags, IReadOnlyList<string> Coatings);

/// <summary>What a loadout may name. <see cref="PendingCores"/> are still importing and join when their import is done.</summary>
public sealed record SpartanCatalog(IReadOnlyList<SpartanCatalogCore> Cores, IReadOnlyList<string> Coatings, IReadOnlyList<string> Visors, IReadOnlyList<string> PendingCores, IReadOnlyList<SpartanCatalogWeapon>? Weapons = null);
