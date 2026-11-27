using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public sealed record CatalogPiece(int Permutation, string? Name);

// A region (or a kit's region) of a core: Key is what `armor` calls it, Region the game's model region id.
public sealed record CatalogRegion(string Kind, string Key, int Region, IReadOnlyList<CatalogPiece> Pieces);

public sealed record CatalogCore(string Name, IReadOnlyList<CatalogRegion> Regions);

// A weapon a stance can hold: Tags are the action pose weaponTagIds that mean it, CoreType the Halo weapon core whose equipped
// theme gives its coating (null for weapons with no customization), Coatings the coating ids of its stylelists.
public sealed record CatalogWeapon(string Name, string? CoreType, IReadOnlyList<int> Tags, IReadOnlyList<string> Coatings);

// A pose a model can carry as a clip (named Name), by its StanceId, and the weapon it holds (null for empty hands).
public sealed record CatalogPose(string Name, int Stance, string? Weapon);

// What a loadout may name: cores with their pieces, coating ids, visor titles, weapons and poses. PendingCores are still importing.
public sealed record Catalog(IReadOnlyList<CatalogCore> Cores, IReadOnlyList<string> Coatings, IReadOnlyList<string> Visors, IReadOnlyList<string> PendingCores, IReadOnlyList<CatalogWeapon> Weapons, IReadOnlyList<CatalogPose> Poses);

internal sealed record PoseEntry(string Pose, int Stance, int? WeaponTag);

internal sealed record WeaponEntry(string Name, string Model, string? CoreType, int[] Tags, int[] Stylelists);

public sealed class CatalogService(IOptions<RenderOptions> options)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private string? _signature;
    private Catalog? _built;
    private string? _weaponsSignature;
    private List<WeaponEntry> _weaponEntries = [];

    // A core's catalog is written next to the cache by the warm-up, and replaced with each Blender script.
    public string PathFor(string core, string renderer)
        => Path.Combine(options.Value.CacheDirectory, "catalog", $"{renderer}-{string.Concat(core.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}.json");

    // Built again only when a file it reads (a core's catalog, a stylelist, the visor list, the weapon list or a weapon model) was added, removed or changed.
    public Catalog Get(string renderer)
    {
        var o = options.Value;
        var signature = Signature(o, renderer);
        lock (_lock)
        {
            if (_built is not null && signature == _signature) return _built;
            _built = Build(o, renderer);
            _signature = signature;
            return _built;
        }
    }

    private string Signature(RenderOptions o, string renderer)
    {
        var files = o.Cores.Select(c => PathFor(c, renderer)).ToList();
        var folder = Path.Combine(o.DataDirectory, "stylelists");
        if (Directory.Exists(folder)) files.AddRange(Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal));
        files.Add(Path.Combine(o.EkurAssets, "all_visors.json"));
        files.Add(o.WeaponsPath);
        files.Add(o.PosesPath);
        files.AddRange(WeaponEntries().Select(w => ModelPath(o.DataDirectory, w.Model)));
        return string.Join('\n', files.Select(FileSignature));
    }

    private Catalog Build(RenderOptions o, string renderer)
    {
        var cores = new List<CatalogCore>();
        var pending = new List<string>();
        foreach (var name in o.Cores)
        {
            var core = ReadCore(PathFor(name, renderer), name);
            if (core is null) pending.Add(name); else cores.Add(core);
        }
        return new Catalog(cores, Coatings(o.DataDirectory), Visors(o.EkurAssets), pending, Weapons(o), Poses(o.PosesPath));
    }

    private static string FileSignature(string f) => new FileInfo(f) is { Exists: true } i ? $"{f}|{i.Length}|{i.LastWriteTimeUtc.Ticks}" : f + "|missing";

    // The weapon list, read again only when its file changes
    private List<WeaponEntry> WeaponEntries()
    {
        var path = options.Value.WeaponsPath;
        var signature = FileSignature(path);
        lock (_lock)
        {
            if (signature != _weaponsSignature)
            {
                _weaponEntries = ReadWeapons(path);
                _weaponsSignature = signature;
            }
            return _weaponEntries;
        }
    }

    // The entry a weapon is rendered from, as text: part of the id of a loadout holding it, so a changed entry renders it again.
    public string WeaponIdentity(string name)
        => WeaponEntries().FirstOrDefault(w => w.Name == name) is { } entry ? JsonSerializer.Serialize(entry, Web) : "";

    private static CatalogCore? ReadCore(string file, string name)
    {
        try { return File.Exists(file) && JsonSerializer.Deserialize<CatalogCore>(File.ReadAllText(file), Web) is { } c ? c with { Name = name } : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    private static string ModelPath(string dataDirectory, string model) => Path.Combine(dataDirectory, "models", model + ".ekur");

    // Each pose with the name of the weapon its weaponTagId means (whether or not that weapon's model is in the data)
    private List<CatalogPose> Poses(string file)
    {
        var byTag = WeaponEntries().SelectMany(w => (w.Tags ?? []).Select(t => (t, w.Name))).GroupBy(p => p.t).ToDictionary(g => g.Key, g => g.First().Name);
        try
        {
            var poses = File.Exists(file) ? JsonSerializer.Deserialize<List<PoseEntry>>(File.ReadAllText(file), Web) ?? [] : [];
            return [.. poses.Select(p => new CatalogPose(p.Pose, p.Stance, p.WeaponTag is int tag && byTag.TryGetValue(tag, out var name) ? name : null))];
        }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }

    private static List<WeaponEntry> ReadWeapons(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<WeaponEntry>>(File.ReadAllText(file), Web) ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }

    // The weapons whose model is in the data folder, each with the coatings of its own stylelists.
    private List<CatalogWeapon> Weapons(RenderOptions o)
        => WeaponEntries()
            .Where(w => File.Exists(ModelPath(o.DataDirectory, w.Model)))
            .Select(w => new CatalogWeapon(w.Name, w.CoreType, w.Tags ?? [], StyleIds((w.Stylelists ?? []).Select(id => Path.Combine(o.DataDirectory, "stylelists", $"{id}.json")))))
            .ToList();

    // The style ids Ekur accepts as a coating: the keys of "styles" in every stylelist (a file that is not readable JSON adds none).
    private static List<string> Coatings(string dataDirectory)
    {
        var folder = Path.Combine(dataDirectory, "stylelists");
        return Directory.Exists(folder) ? StyleIds(Directory.EnumerateFiles(folder, "*.json")) : [];
    }

    private static List<string> StyleIds(IEnumerable<string> stylelists)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in stylelists)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.Object)
                    foreach (var style in styles.EnumerateObject()) ids.Add(style.Name);
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
        return [.. ids];
    }

    // Visors are chosen by title: the keys of Ekur's all_visors.json.
    private static List<string> Visors(string assets)
    {
        var file = Path.Combine(assets, "all_visors.json");
        if (!File.Exists(file)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }
}
