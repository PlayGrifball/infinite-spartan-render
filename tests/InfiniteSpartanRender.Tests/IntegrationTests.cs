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
            WorkDirectory = Path.Combine(_work, "render"),
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
    public async Task APlayersStance_PosesTheGlb()
    {
        var output = Path.Combine(_work, "mark-vii-stance.glb");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Output = "glb", Stance = 39388885 }, output, CancellationToken.None);

        Keep(output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("glTF"));
        Assert.That(bytes.Length, Is.GreaterThan(100_000));
    }

    [Test]
    public async Task AHeadshotInAStance_RendersAPng()
    {
        var output = Path.Combine(_work, "mark-vii-head.png");

        await _runner.RunAsync(new Loadout { Core = "Mark VII", Framing = "head", Stance = 39388885 }, output, CancellationToken.None);

        Keep(output);
        Assert.That((await File.ReadAllBytesAsync(output)).Length, Is.GreaterThan(20_000));
    }
}
