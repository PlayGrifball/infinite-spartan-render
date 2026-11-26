using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public sealed record CatalogPiece(int Permutation, string? Name);

// A region (or a kit's region) of a core: Key is what `armor` calls it, Region the game's model region id.
public sealed record CatalogRegion(string Kind, string Key, int Region, IReadOnlyList<CatalogPiece> Pieces);

public sealed record CatalogCore(string Name, IReadOnlyList<CatalogRegion> Regions);

// What a loadout may name: cores with their pieces, coating ids and visor titles. PendingCores are still importing.
public sealed record Catalog(IReadOnlyList<CatalogCore> Cores, IReadOnlyList<string> Coatings, IReadOnlyList<string> Visors, IReadOnlyList<string> PendingCores);

public sealed class CatalogService(IOptions<RenderOptions> options)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private string? _signature;
    private Catalog? _built;

    // A core's catalog is written next to the cache by the warm-up, and replaced with each Blender script.
    public string PathFor(string core, string renderer)
        => Path.Combine(options.Value.CacheDirectory, "catalog", $"{renderer}-{string.Concat(core.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}.json");

    // Built again only when a file it reads (a core's catalog, a stylelist, the visor list) was added, removed or changed.
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
        return string.Join('\n', files.Select(f => new FileInfo(f) is { Exists: true } i ? $"{f}|{i.Length}|{i.LastWriteTimeUtc.Ticks}" : f + "|missing"));
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
        return new Catalog(cores, Coatings(o.DataDirectory), Visors(o.EkurAssets), pending);
    }

    private static CatalogCore? ReadCore(string file, string name)
    {
        try { return File.Exists(file) && JsonSerializer.Deserialize<CatalogCore>(File.ReadAllText(file), Web) is { } c ? c with { Name = name } : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    // The style ids Ekur accepts as a coating: the keys of "styles" in every stylelist (a file that is not readable JSON adds none).
    private static List<string> Coatings(string dataDirectory)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var folder = Path.Combine(dataDirectory, "stylelists");
        if (!Directory.Exists(folder)) return [];
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
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
