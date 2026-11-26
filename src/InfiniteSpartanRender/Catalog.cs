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
    private IReadOnlyList<string>? _coatings;
    private IReadOnlyList<string>? _visors;

    // A core's catalog is written next to the cache by the warm-up, and replaced with each Blender script.
    public string PathFor(string core, string renderer)
        => Path.Combine(options.Value.CacheDirectory, "catalog", $"{renderer}-{string.Concat(core.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}.json");

    public Catalog Get(string renderer)
    {
        var o = options.Value;
        var cores = new List<CatalogCore>();
        var pending = new List<string>();
        foreach (var name in o.Cores)
        {
            var file = PathFor(name, renderer);
            CatalogCore? core = null;
            if (File.Exists(file))
            {
                try { core = JsonSerializer.Deserialize<CatalogCore>(File.ReadAllText(file), Web) is { } c ? c with { Name = name } : null; }
                catch (JsonException) { }
            }
            if (core is null) pending.Add(name); else cores.Add(core);
        }
        lock (_lock)
        {
            _coatings ??= Coatings(o.DataDirectory);
            _visors ??= Visors(o.EkurAssets);
            return new Catalog(cores, _coatings, _visors, pending);
        }
    }

    // The style ids Ekur accepts as a coating: the keys of "styles" in every stylelist.
    private static List<string> Coatings(string dataDirectory)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var folder = Path.Combine(dataDirectory, "stylelists");
        if (!Directory.Exists(folder)) return [];
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (doc.RootElement.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.Object)
                foreach (var style in styles.EnumerateObject()) ids.Add(style.Name);
        }
        return [.. ids];
    }

    // Visors are chosen by title: the keys of Ekur's all_visors.json.
    private static List<string> Visors(string assets)
    {
        var file = Path.Combine(assets, "all_visors.json");
        if (!File.Exists(file)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList() : [];
    }
}
