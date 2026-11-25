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

        public async Task RunAsync(Loadout loadout, string outputPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(20, cancellationToken);
            if (Fail) throw new InvalidOperationException("blender broke");
            await File.WriteAllTextAsync(outputPath, loadout.Output + loadout.Size, cancellationToken);
        }
    }

    private string _cache = "";
    private FakeRunner _runner = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Path.Combine(Path.GetTempPath(), "isr-" + Guid.NewGuid().ToString("N"));
        _runner = new FakeRunner();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Render:CacheDirectory", _cache);
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

    private static object Body(string output = "png", string size = "small", string core = "Mark VII") =>
        new { core, armor = new Dictionary<string, string> { ["helmet"] = "Mk 7" }, coating = "1120495519", output, size };

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
        Assert.That(status.GetProperty("error").GetString(), Does.Contain("blender broke"));
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
    public async Task AnInvalidLoadout_Gets400_WithoutRendering(string json)
    {
        var response = await _client.PostAsync("/spartans", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_runner.Calls, Is.EqualTo(0));
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
}
