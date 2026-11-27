using System.Security.Cryptography;
using System.Text;

namespace InfiniteSpartanRender;

// One Spartan: a body PNG and a headshot PNG in a chosen stance and in the neutral one, and one GLB with the game rig and both poses as animation clips (neutral, stance).
public sealed record SpartanSetRequest
{
    public const int NeutralStance = -1258174396;

    public string Core { get; init; } = "";
    public Dictionary<string, string>? Armor { get; init; } = new();
    public List<PartId>? Parts { get; init; } = new();
    public string? Coating { get; init; }
    public string? Visor { get; init; }
    public bool DisableDamage { get; init; }
    // A StanceId from a player's action pose item.
    public int? Stance { get; init; }
    public string Size { get; init; } = "small";

    // The five renders: body and head by pose, and the one GLB. A chosen stance equal to the neutral one is the neutral one.
    public IReadOnlyList<(string Name, Loadout Loadout)> Items()
    {
        int? chosen = Stance == NeutralStance ? null : Stance;
        var items = new List<(string, Loadout)>();
        foreach (var (pose, stance) in new[] { ("stance", chosen), ("neutral", (int?)null) })
        {
            var basis = new Loadout { Core = Core, Armor = Armor, Parts = Parts, Coating = Coating, Visor = Visor, DisableDamage = DisableDamage, Size = Size, Stance = stance };
            items.Add(($"{pose}-body", basis with { Output = "png", Framing = "body" }));
            items.Add(($"{pose}-head", basis with { Output = "png", Framing = "head" }));
        }
        items.Add(("glb", new Loadout { Core = Core, Armor = Armor, Parts = Parts, Coating = Coating, Visor = Visor, DisableDamage = DisableDamage, Size = Size, Stance = chosen, Output = "glb" }));
        return items;
    }

    public string? Validate(IReadOnlyCollection<string> cores, IReadOnlyCollection<string>? visors = null)
    {
        if (Stance is null) return "stance is required";
        return Items()[0].Loadout.Validate(cores, visors);
    }

    public string Id(string renderer = "") => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', Items().Select(i => i.Loadout.Id(renderer))))))[..32].ToLowerInvariant();
}

public sealed record SpartanSetItem(string Name, string Id, string Output, JobStatus Status);

public sealed record SpartanSetStatus(string Id, string State, IReadOnlyList<SpartanSetItem> Items);
