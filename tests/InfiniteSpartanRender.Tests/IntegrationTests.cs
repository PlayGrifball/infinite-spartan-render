using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender.Tests;

// Renders for real with Blender, Ekur and the game data volume, so they run only on the in-cluster runner
// (ISR_INTEGRATION=1, set by its CI job), never on GitHub's hosted runners.
[TestFixture]
[Category("Integration")]
public class IntegrationTests
{
    private string _work = "";
    private BlenderRunner _runner = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        if (Environment.GetEnvironmentVariable("ISR_INTEGRATION") != "1")
            Assert.Ignore("Needs Blender and the game data: set ISR_INTEGRATION=1 on the in-cluster runner.");

        _work = Path.Combine(Path.GetTempPath(), "isr-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InfiniteSpartanRender.slnx"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "InfiniteSpartanRender.slnx not found above the test output");

        _runner = new BlenderRunner(Options.Create(new RenderOptions
        {
            ScriptPath = Path.Combine(dir!.FullName, "blender", "spartan.py"),
            WeaponsPath = Path.Combine(dir!.FullName, "blender", "weapons.json"),
            WorkDirectory = Path.Combine(_work, "render"),
            CacheDirectory = Path.Combine(_work, "cache"),
            TimeoutSeconds = 1800,
        }));
    }

    private readonly List<(string Test, string Phase, double Seconds)> _phases = new();
    private string _current = "";
    private System.Diagnostics.Metrics.MeterListener? _listener;

    [SetUp]
    public void StartTimingTest()
    {
        _current = TestContext.CurrentContext.Test.Name;
        _listener = new System.Diagnostics.Metrics.MeterListener();
        _listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == RenderTelemetry.Name && i.Name == "spartan_render.phase_duration") l.EnableMeasurementEvents(i); };
        _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var phase = tags.ToArray().First(t => t.Key == "phase").Value?.ToString() ?? "?";
            lock (_phases) _phases.Add((_current, phase, value));
        });
        _listener.Start();
    }

    [TearDown]
    public void StopTimingTest()
    {
        _listener?.Dispose();
        _listener = null;
    }

    // One table of where each render's time went, in the job summary and next to the renders.
    private void WriteTimings()
    {
        var lines = new List<string> { "### Render phases", "", "| test | phase | samples | seconds |", "|---|---|---|---|" };
        lock (_phases)
        {
            if (_phases.Count == 0) return;
            foreach (var g in _phases.GroupBy(p => (p.Test, p.Phase)))
                lines.Add($"| {g.Key.Test} | {g.Key.Phase} | {g.Count()} | {g.Sum(p => p.Seconds):F1} |");
        }
        var text = string.Join('\n', lines) + "\n";
        TestContext.Progress.WriteLine(text);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllText(summary, text);
        if (Environment.GetEnvironmentVariable("ISR_ARTIFACTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "timings.md"), text);
        }
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        WriteTimings();
        // Three tests render Mark VII: the first imports it (minutes), the others open the saved import.
        double[] imports;
        lock (_phases) imports = _phases.Where(p => p.Phase == "import" && !p.Test.Contains("Yoroi") && !p.Test.Contains("WithoutABodyType") && !p.Test.Contains("Warm") && !p.Test.Contains("Visor") && !p.Test.Contains("Weapon")).Select(p => p.Seconds).ToArray();
        if (imports.Length == 3) Assert.That(imports.Count(s => s < 60), Is.GreaterThanOrEqualTo(2), "a core's import is reused: " + string.Join(", ", imports));
        if (_work.Length > 0 && Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    // The CI job uploads this folder, so a person can look at the renders.
    private static void Keep(string output)
    {
        var folder = Environment.GetEnvironmentVariable("ISR_ARTIFACTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        File.Copy(output, Path.Combine(folder, Path.GetFileName(output)), overwrite: true);
    }

    [Test]
    public async Task ADefaultMarkVII_RendersAPng()
    {
        var output = Path.Combine(_work, "mark-vii.png");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "png", Size = "small" }, output, CancellationToken.None);

        Keep(output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.That(bytes.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That(bytes.Length, Is.GreaterThan(20_000), "a Spartan, not an empty frame");
        Assert.That(CornerAlpha(bytes), Is.EqualTo(0), "the background is transparent");
    }

    // Alpha of the top-left pixel of an 8-bit RGBA PNG. On the first row every filter predicts from zeros, so the stored bytes are the pixel.
    private static int CornerAlpha(byte[] png)
    {
        Assert.That((bytes: png[24], type: png[25]), Is.EqualTo((bytes: (byte)8, type: (byte)6)), "8-bit RGBA");
        using var idat = new MemoryStream();
        for (var at = 8; at + 8 <= png.Length;)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            if (System.Text.Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT") idat.Write(png, at + 8, length);
            at += 12 + length;
        }
        idat.Position = 0;
        using var inflate = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        var first = new byte[5];
        inflate.ReadExactly(first);
        return first[4];
    }

    [Test]
    public async Task ACoreWithoutABodyTypeCollection_ExportsAGlb()
    {
        var output = Path.Combine(_work, "yoroi.glb");

        await _runner.RunAsync(new Loadout { Core = "Yoroi", Output = "glb" }, output, CancellationToken.None);

        Keep(output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("glTF"));
        Assert.That(bytes.Length, Is.GreaterThan(100_000));
    }

    [Test]
    public async Task AVisor_ChangesTheRenderedHelmet()
    {
        var plain = Path.Combine(_work, "visor-default.png");
        var visored = Path.Combine(_work, "visor-faze.png");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "png", Framing = "head" }, plain, CancellationToken.None);
        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "png", Framing = "head", Visor = "2024 FaZE Launch" }, visored, CancellationToken.None);

        Keep(visored);
        Assert.That(await File.ReadAllBytesAsync(visored), Is.Not.EqualTo(await File.ReadAllBytesAsync(plain)), "the visor is in the picture");
    }

    [Test]
    public async Task AWeapon_IsHeldInTheStanceRender()
    {
        var empty = Path.Combine(_work, "weapon-none.png");
        var held = Path.Combine(_work, "weapon-commando.png");
        var stance = new Loadout { Core = "Mark VII", Output = "png", Stance = 1756613951 };

        await _runner.RunAsync(stance, empty, CancellationToken.None);
        await _runner.RunAsync(stance with { Weapon = "VK78 Commando Rifle", WeaponCoating = "-976095822" }, held, CancellationToken.None);

        Keep(held);
        Assert.That(await File.ReadAllBytesAsync(held), Is.Not.EqualTo(await File.ReadAllBytesAsync(empty)), "the rifle is in the picture");
    }

    [Test]
    public async Task AGlb_KeepsTheRigWithAClipForEachPose()
    {
        var output = Path.Combine(_work, "mark-vii-stance.glb");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "glb", Stance = 39388885 }, output, CancellationToken.None);

        Keep(output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("glTF"));
        var jsonLength = BitConverter.ToInt32(bytes, 12);
        var json = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes, 20, jsonLength)).RootElement;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json.GetProperty("skins").GetArrayLength(), Is.GreaterThan(0), "the game rig is kept");
            Assert.That(json.GetProperty("animations").EnumerateArray().Select(a => a.GetProperty("name").GetString()), Is.EquivalentTo(Clips));
            Assert.That(bytes.Length, Is.GreaterThan(100_000));
        }
    }

    private static readonly string[] Clips = ["neutral", "stance"];

    [Test]
    public async Task TheSameArmorInAnotherStance_ReusesTheBakedTextures()
    {
        var posed = Path.Combine(_work, "reuse-posed.glb");
        var neutral = Path.Combine(_work, "reuse-neutral.glb");
        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "glb", Stance = 39388885 }, posed, CancellationToken.None);
        int mark;
        lock (_phases) mark = _phases.Count;

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "glb" }, neutral, CancellationToken.None);

        double baked, reused;
        lock (_phases)
        {
            var second = _phases.Skip(mark).ToArray();
            baked = second.Where(p => p.Phase == "bake").Sum(p => p.Seconds);
            reused = second.Where(p => p.Phase == "bake_reused").Sum(p => p.Seconds);
        }
        Keep(posed);
        Keep(neutral);
        Assert.That(baked, Is.EqualTo(0), "the second GLB bakes nothing");
        Assert.That(reused, Is.GreaterThan(0), "and loads the first one's textures");
        Assert.That(System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(neutral), 0, 4), Is.EqualTo("glTF"));
        Assert.That(new FileInfo(neutral).Length, Is.GreaterThan(100_000));
    }

    [Test]
    public async Task AWarmedCore_IsReusedByTheFirstRender()
    {
        var marker = Path.Combine(_work, "warm.warm");
        await _runner.RunAsync(new Loadout { Core = "Mark IV", Output = "warm" }, marker, CancellationToken.None);
        int mark;
        lock (_phases) mark = _phases.Count;

        var output = Path.Combine(_work, "mark-iv.png");
        await _runner.RunAsync(new Loadout { Core = "Mark IV", Output = "png", Size = "small" }, output, CancellationToken.None);

        double import;
        lock (_phases) import = _phases.Skip(mark).Where(p => p.Phase == "import").Sum(p => p.Seconds);
        Assert.That(import, Is.LessThan(60), "the render opens the warmed import instead of making it");
        Assert.That(new FileInfo(output).Length, Is.GreaterThan(20_000));
    }

    [Test]
    public async Task WarmingACoreThatIsAlreadySaved_OpensNothing()
    {
        var marker = Path.Combine(_work, "again.warm");
        await _runner.RunAsync(new Loadout { Core = "Mark IV", Output = "warm" }, marker, CancellationToken.None);
        int mark;
        lock (_phases) mark = _phases.Count;

        await _runner.RunAsync(new Loadout { Core = "Mark IV", Output = "warm" }, Path.Combine(_work, "again2.warm"), CancellationToken.None);

        string[] phases;
        lock (_phases) phases = _phases.Skip(mark).Select(p => p.Phase).ToArray();
        Assert.That(phases, Is.EqualTo(new[] { "import_cached" }), "only the check ran, no import or open");
    }

    // The mapping gives a piece's permutation display name; the name-based `armor` input finds the object by it.
    [Test]
    public async Task ImportedObjects_CarryTheIdsAndTheirNamesEndInThePermutationName()
    {
        var output = Path.Combine(_work, "names-mark-vii.json");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "names" }, output, CancellationToken.None);

        Keep(output);
        var rows = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(output)).RootElement.EnumerateArray().ToList();
        Assert.That(rows, Is.Not.Empty);
        var noIds = rows.Where(r => r.GetProperty("region").ValueKind == System.Text.Json.JsonValueKind.Null || r.GetProperty("permutation").ValueKind == System.Text.Json.JsonValueKind.Null).Select(r => r.GetProperty("object").GetString()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(noIds, Is.Empty, "objects without region_name/permutation_name: parts cannot select them");
            // Shoulder pads all carry one permutation id with no display name: the ids cannot tell them apart, so only the name does.
            var named = rows.Where(r => !r.GetProperty("hashed").GetBoolean() && !string.IsNullOrEmpty(r.GetProperty("display").GetString())).ToList();
            var mismatched = named.Where(r => !r.GetProperty("nameMatches").GetBoolean()).Select(r => $"[{r.GetProperty("kind").GetString()}] {r.GetProperty("object").GetString()} (permutation {r.GetProperty("permutation")}, display '{r.GetProperty("display").GetString()}')").ToList();
            Assert.That(string.Join("\n", mismatched), Is.Empty, "named objects whose name does not end in the permutation's display name; of " + named.Count);
            Assert.That(rows.Count(r => r.GetProperty("truncated").GetBoolean()), Is.Zero, "object names cut at Blender's 63 characters");
        }
    }

    [Test]
    public async Task TheCatalogOfACore_ListsRegionsWithNamedAndUnnamedPieces()
    {
        var output = Path.Combine(_work, "catalog-mark-vii.json");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "catalog" }, output, CancellationToken.None);

        Keep(output);
        var regions = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(output)).RootElement.GetProperty("regions").EnumerateArray().ToList();
        Assert.That(regions.Select(r => r.GetProperty("key").GetString()), Does.Contain("helmet"));
        var helmet = regions.First(r => r.GetProperty("key").GetString() == "helmet");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(helmet.GetProperty("pieces").GetArrayLength(), Is.GreaterThan(1), "a core offers more than its own helmet");
            Assert.That(helmet.GetProperty("pieces").EnumerateArray().Select(p => p.GetProperty("name").GetString()), Does.Contain("MARK VII").IgnoreCase, "helmet pieces: " + string.Join(" | ", helmet.GetProperty("pieces").EnumerateArray().Select(p => p.GetProperty("name").GetString() + "#" + p.GetProperty("permutation"))));
        }
    }

    [Test]
    public async Task APieceByIds_ExportsAGlb()
    {
        // A piece of the core's own catalog, an unnamed one first: those are only addressable by region and permutation id.
        var catalogFile = Path.Combine(_work, "catalog-for-ids.json");
        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "catalog" }, catalogFile, CancellationToken.None);
        var pieces = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(catalogFile)).RootElement.GetProperty("regions").EnumerateArray()
            .SelectMany(r => r.GetProperty("pieces").EnumerateArray().Select(p => (Region: r.GetProperty("region").GetInt32(), Permutation: p.GetProperty("permutation").GetInt32(), Named: p.GetProperty("name").ValueKind == System.Text.Json.JsonValueKind.String)))
            .OrderBy(p => p.Named).ToList();
        var piece = pieces.First();
        var output = Path.Combine(_work, "piece-by-ids.glb");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "glb", Parts = new() { new(piece.Region, piece.Permutation) } }, output, CancellationToken.None);

        Keep(output);
        Assert.That(System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(output), 0, 4), Is.EqualTo("glTF"));
    }

    [Test]
    public void APieceThatMatchesNothing_FailsTheRender()
    {
        var output = Path.Combine(_work, "no-such-part.glb");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "glb", Parts = new() { new(1, 2) } }, output, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("No mesh for parts"));
    }

    [Test]
    public async Task AHeadshotInAStance_RendersAPng()
    {
        var output = Path.Combine(_work, "mark-vii-head.png");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Framing = "head", Stance = 39388885 }, output, CancellationToken.None);

        Keep(output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.That(bytes.Length, Is.GreaterThan(20_000));
        Assert.That(CornerAlpha(bytes), Is.EqualTo(0), "the background is transparent");
    }
}
