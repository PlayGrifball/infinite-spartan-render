using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InfiniteSpartanRender.Tests;

public class ApiTests
{
    private sealed class FakeRunner : IBlenderRunner
    {
        public int Calls;
        public bool Fail;
        public bool FailWarm;
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Warmed = new();

        public Task? Gate;
        public string? LastPath;
        public readonly System.Collections.Concurrent.ConcurrentBag<Loadout> Loadouts = new();

        public async Task RunAsync(Loadout loadout, string outputPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Loadouts.Add(loadout);
            if (loadout.Output == "warm")
            {
                if (FailWarm) throw new InvalidOperationException("warm broke");
                Warmed.Add(loadout.Core);
                await File.WriteAllTextAsync(outputPath, "warm", cancellationToken);
                return;
            }
            LastPath = outputPath;
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            await Task.Delay(20, cancellationToken);
            if (Fail) throw new InvalidOperationException("blender broke");
            await File.WriteAllTextAsync(outputPath, loadout.Output + loadout.Size, cancellationToken);
        }
    }

    private string _cache = "";
    private readonly Dictionary<string, string> _settings = new();
    private bool _failWarm;
    private FakeRunner _runner = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _settings.Clear();
        _failWarm = false;
        Start();
    }

    private void Start()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _cache = Path.Combine(Path.GetTempPath(), "isr-" + Guid.NewGuid().ToString("N"));
        _runner = new FakeRunner { FailWarm = _failWarm };
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Render:CacheDirectory", _cache);
            b.UseSetting("Render:WarmImports", "false");
            foreach (var (k, v) in _settings) b.UseSetting(k, v);
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IBlenderRunner>(_runner)));
        });
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
    }

    private static object Body(string output = "png", string size = "small", string core = "Mark VII", string coating = "1120495519") =>
        new { core, armor = new Dictionary<string, string> { ["helmet"] = "Mk 7" }, coating, output, size };

    private async Task<string> StateOf(string id, string wanted)
    {
        for (var i = 0; i < 100; i++)
        {
            var doc = await _client.GetFromJsonAsync<JsonElement>($"/spartans/{id}");
            var state = doc.GetProperty("state").GetString()!;
            if (state == wanted) return state;
            await Task.Delay(20);
        }
        return "timeout";
    }

    [Test]
    public async Task APostedLoadout_IsAccepted_RendersAndServesTheFile()
    {
        var response = await _client.PostAsJsonAsync("/spartans", Body());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.That(await StateOf(id, "done"), Is.EqualTo("done"));
        var file = await _client.GetAsync($"/spartans/{id}/file");
        Assert.That(file.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(file.Content.Headers.ContentType!.MediaType, Is.EqualTo("image/png"));
        Assert.That(await file.Content.ReadAsStringAsync(), Is.EqualTo("pngsmall"));
    }

    [Test]
    public async Task AGlbRequest_IsServedAsGltfBinary()
    {
        var id = (await (await _client.PostAsJsonAsync("/spartans", Body("glb", "full"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await StateOf(id, "done");

        var file = await _client.GetAsync($"/spartans/{id}/file");

        Assert.That(file.Content.Headers.ContentType!.MediaType, Is.EqualTo("model/gltf-binary"));
        Assert.That(await file.Content.ReadAsStringAsync(), Is.EqualTo("glbfull"));
    }

    [Test]
    public async Task TheSameLoadoutTwice_RendersOnce()
    {
        var first = await (await _client.PostAsJsonAsync("/spartans", Body())).Content.ReadFromJsonAsync<JsonElement>();
        var id = first.GetProperty("id").GetString()!;
        await StateOf(id, "done");

        var second = await (await _client.PostAsJsonAsync("/spartans", Body())).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(second.GetProperty("id").GetString(), Is.EqualTo(id));
        Assert.That(second.GetProperty("state").GetString(), Is.EqualTo("done"));
        Assert.That(_runner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task SmallAndFull_AreDifferentJobs()
    {
        var small = await (await _client.PostAsJsonAsync("/spartans", Body(size: "small"))).Content.ReadFromJsonAsync<JsonElement>();
        var full = await (await _client.PostAsJsonAsync("/spartans", Body(size: "full"))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(small.GetProperty("id").GetString(), Is.Not.EqualTo(full.GetProperty("id").GetString()));
    }

    [Test]
    public async Task AFailedRender_ReportsTheError_AndCanBeRetried()
    {
        _runner.Fail = true;
        var id = (await (await _client.PostAsJsonAsync("/spartans", Body())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.That(await StateOf(id, "failed"), Is.EqualTo("failed"));
        var status = await _client.GetFromJsonAsync<JsonElement>($"/spartans/{id}");
        Assert.That(status.GetProperty("error").GetString(), Is.EqualTo(RenderService.FailureMessage));
        Assert.That((await _client.GetAsync($"/spartans/{id}/file")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        _runner.Fail = false;
        await _client.PostAsJsonAsync("/spartans", Body());
        Assert.That(await StateOf(id, "done"), Is.EqualTo("done"));
    }

    [TestCase("""{"core":"","output":"png","size":"small"}""")]
    [TestCase("""{"core":"Mark VII","output":"obj","size":"small"}""")]
    [TestCase("""{"core":"Mark VII","output":"png","size":"huge"}""")]
    [TestCase("""{"core":"Mark VII","coating":"1; rm -rf /","output":"png","size":"small"}""")]
    [TestCase("""{"core":"Mark VII","coating":"12345678901234","output":"png","size":"small"}""")]
    [TestCase("""{"core":"Mark VII","framing":"legs"}""")]
    [TestCase("""{"core":"Mark VII","stance":"1; rm -rf /"}""")]
    public async Task AnInvalidLoadout_Gets400_WithoutRendering(string json)
    {
        var response = await _client.PostAsync("/spartans", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_runner.Calls, Is.EqualTo(0));
    }

    private static object SetBody(int? stance = 39388885, string core = "Mark VII") =>
        new { core, armor = new Dictionary<string, string> { ["helmet"] = "Mk 7" }, coating = "1120495519", stance, size = "small" };

    private async Task<JsonElement> SetWhenDone(string id)
    {
        for (var i = 0; i < 200; i++)
        {
            var doc = await _client.GetFromJsonAsync<JsonElement>($"/spartan-sets/{id}");
            if (doc.GetProperty("state").GetString() is "done" or "failed") return doc;
            await Task.Delay(20);
        }
        throw new TimeoutException("the set never finished");
    }

    [Test]
    public async Task ASet_RendersTwoGlbsAndFourPngs_InTheStanceAndTheNeutralPose()
    {
        var response = await _client.PostAsJsonAsync("/spartan-sets", SetBody());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var set = await SetWhenDone(id);

        Assert.That(set.GetProperty("state").GetString(), Is.EqualTo("done"));
        var items = set.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
        Assert.That(items.Keys, Is.EquivalentTo(new[] { "stance-glb", "neutral-glb", "stance-body", "stance-head", "neutral-body", "neutral-head" }));
        Assert.That(items.Values.Select(i => i.GetProperty("url").GetString()).Distinct().Count(), Is.EqualTo(6));
        foreach (var item in items.Values)
            Assert.That((await _client.GetAsync(item.GetProperty("url").GetString())).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_runner.Calls, Is.EqualTo(6));
    }

    [Test]
    public async Task ASetsNeutralItems_UseTheDefaultStance_AndItsOtherItemsTheChosenOne()
    {
        var id = (await (await _client.PostAsJsonAsync("/spartan-sets", SetBody(5))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await SetWhenDone(id);

        Assert.That(_runner.Loadouts.Count(l => l.Stance is null), Is.EqualTo(3));
        Assert.That(_runner.Loadouts.Count(l => l.Stance == 5), Is.EqualTo(3));
        Assert.That(_runner.Loadouts.Count(l => l.Output == "glb"), Is.EqualTo(2));
        Assert.That(_runner.Loadouts.Count(l => l.Framing == "head"), Is.EqualTo(2));
    }

    [Test]
    public async Task ASetPostedTwice_RendersOnce()
    {
        var id = (await (await _client.PostAsJsonAsync("/spartan-sets", SetBody())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await SetWhenDone(id);

        var again = await (await _client.PostAsJsonAsync("/spartan-sets", SetBody())).Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(again.GetProperty("id").GetString(), Is.EqualTo(id));
        Assert.That(again.GetProperty("state").GetString(), Is.EqualTo("done"));
        Assert.That(_runner.Calls, Is.EqualTo(6));
    }

    [Test]
    public async Task ASetWithAFailedRender_IsFailed()
    {
        _runner.Fail = true;
        var id = (await (await _client.PostAsJsonAsync("/spartan-sets", SetBody())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.That((await SetWhenDone(id)).GetProperty("state").GetString(), Is.EqualTo("failed"));
    }

    [TestCase("""{"core":"Mark VII"}""", Description = "no stance")]
    [TestCase("""{"core":"Mark VII","stance":"1; rm -rf /"}""")]
    [TestCase("""{"core":"Nope","stance":1}""")]
    [TestCase("""{"core":"Mark VII","stance":1,"size":"huge"}""")]
    public async Task AnInvalidSet_Gets400_WithoutRendering(string json)
    {
        var response = await _client.PostAsync("/spartan-sets", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_runner.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task AChangedRenderScript_ChangesTheId_SoOldFilesAreNotServed()
    {
        var script = Path.Combine(Path.GetTempPath(), "isr-script-" + Guid.NewGuid().ToString("N") + ".py");
        try
        {
            File.WriteAllText(script, "print(1)");
            _settings["Render:ScriptPath"] = script;
            Start();
            var before = (await (await _client.PostAsJsonAsync("/spartans", Body())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

            File.WriteAllText(script, "print(2)");
            Start();
            var after = (await (await _client.PostAsJsonAsync("/spartans", Body())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

            Assert.That(after, Is.Not.EqualTo(before));
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    public async Task AnUnknownSet_Is404()
    {
        Assert.That((await _client.GetAsync("/spartan-sets/nope")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync("/spartan-sets/" + new string('a', 32))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AnUnknownJob_Is404()
    {
        Assert.That((await _client.GetAsync("/spartans/nope")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync("/spartans/nope/file")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AnIdWithPathCharacters_IsNeverReadFromTheCache()
    {
        Directory.CreateDirectory(_cache);
        await File.WriteAllTextAsync(Path.Combine(_cache, "x.png"), "secret");

        var response = await _client.GetAsync("/spartans/..%2Fx/file");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
    [Test]
    public async Task TheRunnerIsGivenAPathWithTheRealExtension()
    {
        var png = (await (await _client.PostAsJsonAsync("/spartans", Body())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await StateOf(png, "done");
        Assert.That(_runner.LastPath, Does.EndWith(".png"));

        var glb = (await (await _client.PostAsJsonAsync("/spartans", Body("glb"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await StateOf(glb, "done");
        Assert.That(_runner.LastPath, Does.EndWith(".glb"));
    }

    [TestCase("""{"core":"Mark VII","armor":null}""")]
    [TestCase("""{"core":"Mark VII","armor":{"helmet":null}}""")]
    [TestCase("""{"core":"Not A Core"}""")]
    public async Task NullOrUnknownMembers_Get400_NotAServerError(string json)
    {
        var response = await _client.PostAsync("/spartans", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_runner.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task AFullQueue_Gets503WithRetryAfter()
    {
        var gate = new TaskCompletionSource();
        _runner.Gate = gate.Task;
        _settings["Render:MaxQueued"] = "1";
        _settings["Render:MaxConcurrentJobs"] = "1";
        Start();
        _runner.Gate = gate.Task;

        var running = (await (await _client.PostAsJsonAsync("/spartans", Body(core: "Yoroi"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await StateOf(running, "running");
        var queued = await _client.PostAsJsonAsync("/spartans", Body(core: "Chimera"));
        var rejected = await _client.PostAsJsonAsync("/spartans", Body(core: "HAZMAT"));

        Assert.That(queued.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(rejected.Headers.RetryAfter, Is.Not.Null);
        gate.SetResult();
    }

    [Test]
    public async Task TooManySubmitsFromOneAddress_Get429()
    {
        _settings["Render:SubmitsPerMinute"] = "2";
        Start();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            codes.Add((await _client.PostAsJsonAsync("/spartans", Body(coating: i.ToString()))).StatusCode);

        Assert.That(codes[2], Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [Test]
    public async Task WhenEnabled_TheServiceImportsEveryCore_BeforeAnyoneAsks()
    {
        _settings["Render:WarmImports"] = "true";
        Start();

        for (var i = 0; i < 100 && _runner.Warmed.Count < new RenderOptions().Cores.Length; i++) await Task.Delay(50);

        Assert.That(_runner.Warmed.OrderBy(c => c), Is.EqualTo(new RenderOptions().Cores.OrderBy(c => c)));
        Assert.That(Directory.EnumerateFiles(_cache, "*warm*"), Is.Empty, "the marker files are removed");
    }

    [Test]
    public async Task AFailedWarm_DoesNotStopTheServiceRendering()
    {
        _settings["Render:WarmImports"] = "true";
        _failWarm = true;
        Start();

        var response = await _client.PostAsJsonAsync("/spartans", Body());
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.That(await StateOf(id, "done"), Is.EqualTo("done"));
    }

    [Test]
    public async Task TheSweep_DropsOldCacheFiles_AndKeepsNewOnes()
    {
        Directory.CreateDirectory(_cache);
        var old = Path.Combine(_cache, new string('0', 32) + ".png");
        var fresh = Path.Combine(_cache, new string('1', 32) + ".png");
        await File.WriteAllTextAsync(old, "x");
        await File.WriteAllTextAsync(fresh, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));

        _factory.Services.GetRequiredService<RenderService>().Sweep(DateTimeOffset.UtcNow);

        Assert.That(File.Exists(old), Is.False);
        Assert.That(File.Exists(fresh), Is.True);
    }

    [Test]
    public async Task TheSweep_DropsOldImportedCores_InTheImportsFolder()
    {
        var imports = Path.Combine(_cache, "imports");
        Directory.CreateDirectory(imports);
        var old = Path.Combine(imports, "mark-vii-aaaa.blend");
        var fresh = Path.Combine(imports, "yoroi-bbbb.blend");
        await File.WriteAllTextAsync(old, "x");
        await File.WriteAllTextAsync(fresh, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));

        _factory.Services.GetRequiredService<RenderService>().Sweep(DateTimeOffset.UtcNow);

        Assert.That(File.Exists(old), Is.False);
        Assert.That(File.Exists(fresh), Is.True);
    }

    [Test]
    public async Task AnUnknownJob_TellsTheClientToPostAgain()
    {
        var response = await _client.GetAsync("/spartans/" + new string('a', 32));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("POST the loadout again"));
    }
}

public class LoadoutTests
{
    [Test]
    public void ArmorOrder_DoesNotChangeTheId()
    {
        var a = new Loadout { Core = "Mark VII", Armor = new() { ["helmet"] = "a", ["chest"] = "b" } };
        var b = new Loadout { Core = "Mark VII", Armor = new() { ["chest"] = "b", ["helmet"] = "a" } };

        Assert.That(a.Id(), Is.EqualTo(b.Id()));
    }

    [Test]
    public void ADifferentCoating_ChangesTheId()
    {
        Assert.That(new Loadout { Core = "x", Coating = "1" }.Id(), Is.Not.EqualTo(new Loadout { Core = "x", Coating = "2" }.Id()));
    }

    [Test]
    public void ADifferentFraming_ChangesTheId()
    {
        Assert.That(new Loadout { Core = "x", Framing = "body" }.Id(), Is.Not.EqualTo(new Loadout { Core = "x", Framing = "head" }.Id()));
    }

    [Test]
    public void ADifferentStance_ChangesTheId()
    {
        Assert.That(new Loadout { Core = "x", Stance = 1 }.Id(), Is.Not.EqualTo(new Loadout { Core = "x", Stance = 2 }.Id()));
    }

    [Test]
    public void NoStance_IsTheDefaultStance()
    {
        Assert.That(new Loadout { Core = "x" }.Id(), Is.EqualTo(new Loadout { Core = "x", Stance = null }.Id()));
    }
}

public class BlenderRunnerTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "isr-run-" + Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    [Test]
    public async Task BlenderIsToldWhereToKeepImportedCoresAndBakedTextures_UnderTheCache()
    {
        var output = Path.Combine(_dir, "out.png");
        var runner = RunnerFor("for last; do :; done; echo \"$ISR_IMPORT_CACHE $ISR_BAKE_CACHE\" > \"$last\"", cache: Path.Combine(_dir, "cache"));

        await runner.RunAsync(Mark7, output, CancellationToken.None);

        Assert.That((await File.ReadAllTextAsync(output)).Trim(), Is.EqualTo($"{Path.Combine(_dir, "cache", "imports")} {Path.Combine(_dir, "cache", "bakes")}"));
    }

    private BlenderRunner RunnerFor(string script, int timeoutSeconds = 30, string cache = "/cache")
    {
        var path = Path.Combine(_dir, "blender");
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new BlenderRunner(Microsoft.Extensions.Options.Options.Create(new RenderOptions
        {
            BlenderPath = path,
            CacheDirectory = cache,
            WorkDirectory = Path.Combine(_dir, "work"),
            TimeoutSeconds = timeoutSeconds,
        }));
    }

    private static Loadout Mark7 => new() { Core = "Mark VII" };

    [Test]
    public async Task ABlenderThatWritesTheOutput_Succeeds_AndLeavesNoWorkDirectory()
    {
        var output = Path.Combine(_dir, "out.png");
        var runner = RunnerFor("for last; do :; done; echo done > \"$last\"");

        await runner.RunAsync(Mark7, output, CancellationToken.None);

        Assert.That(File.Exists(output), Is.True);
        Assert.That(Directory.GetDirectories(Path.Combine(_dir, "work")), Is.Empty);
    }

    [TestCase("ISR_PHASE bake 12.50", "bake", 12.5)]
    [TestCase("ISR_PHASE import 0.01", "import", 0.01)]
    public void APhaseLine_IsParsed(string line, string phase, double seconds)
    {
        Assert.That(PhaseTiming.TryParse(line, out var p, out var s), Is.True);
        Assert.That((p, s), Is.EqualTo((phase, seconds)));
    }

    [TestCase("")]
    [TestCase("MATS 4 missing []")]
    [TestCase("ISR_PHASE bake")]
    [TestCase("ISR_PHASE bake abc")]
    [TestCase("ISR_PHASE bake -1")]
    [TestCase("ISR_PHASE Bake-Helmet 1")]
    [TestCase("ISR_PHASE bake 1 extra")]
    public void ALineThatIsNotAPhase_IsIgnored(string line)
    {
        Assert.That(PhaseTiming.TryParse(line, out _, out _), Is.False);
    }

    [Test]
    public async Task EachPhaseBlenderReports_IsRecordedByPhaseAndOutput_AndKeptOutOfTheErrorTail()
    {
        var seen = new List<(string Phase, string Output, double Seconds)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == RenderTelemetry.Name && i.Name == "spartan_render.phase_duration") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            lock (seen) seen.Add((tags.ToArray().Single(t => t.Key == "phase").Value!.ToString()!, tags.ToArray().Single(t => t.Key == "output").Value!.ToString()!, value));
        });
        listener.Start();
        var runner = RunnerFor("echo ISR_PHASE import 1.5; echo ISR_PHASE bake 7; echo not a phase; exit 4");

        var e = Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Mark7 with { Output = "glb" }, Path.Combine(_dir, "out.glb"), CancellationToken.None));

        lock (seen) Assert.That(seen, Is.EquivalentTo(new[] { ("import", "glb", 1.5), ("bake", "glb", 7.0) }));
        Assert.That(e!.Message, Does.Contain("not a phase").And.Not.Contain("ISR_PHASE"));
    }

    [Test]
    public void ABlenderThatExitsNonZero_ThrowsWithItsOutput()
    {
        var runner = RunnerFor("echo something broke; exit 3");

        var e = Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Mark7, Path.Combine(_dir, "out.png"), CancellationToken.None));

        Assert.That(e!.Message, Does.Contain("exited with 3").And.Contain("something broke"));
    }

    [Test]
    public void ABlenderThatExitsZeroWithoutTheFile_Throws()
    {
        var runner = RunnerFor("exit 0");

        Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Mark7, Path.Combine(_dir, "out.png"), CancellationToken.None));
    }

    [Test]
    public void ABlenderThatRunsTooLong_IsKilled_AsATimeout()
    {
        var runner = RunnerFor("exec sleep 30", timeoutSeconds: 1);

        Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(Mark7, Path.Combine(_dir, "out.png"), CancellationToken.None));
    }

    [Test]
    public void AShutdown_IsACancellation_NotATimeout()
    {
        var runner = RunnerFor("exec sleep 30");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        Assert.CatchAsync<OperationCanceledException>(() => runner.RunAsync(Mark7, Path.Combine(_dir, "out.png"), cts.Token));
    }
}
