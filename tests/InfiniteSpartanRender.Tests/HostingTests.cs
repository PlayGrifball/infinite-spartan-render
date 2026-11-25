using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InfiniteSpartanRender.Tests;

public class HostingTests
{
    private sealed class OkRunner : IBlenderRunner
    {
        public Task RunAsync(Loadout loadout, string outputPath, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(outputPath, "x", cancellationToken);
    }

    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "isr-host-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private WebApplicationFactory<Program> Factory(string blender, bool withData) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            Directory.CreateDirectory(Path.Combine(_dir, "data"));
            if (withData) File.WriteAllText(Path.Combine(_dir, "data", "customization_globals.json"), "{}");
            b.UseSetting("Render:CacheDirectory", Path.Combine(_dir, "cache"));
            b.UseSetting("Render:DataDirectory", Path.Combine(_dir, "data"));
            b.UseSetting("Render:BlenderPath", blender);
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IBlenderRunner>(new OkRunner())));
        });

    [Test]
    public async Task Live_IsHealthy_EvenWhenNothingIsMounted()
    {
        using var factory = Factory("/nonexistent/blender", withData: false);
        var response = await factory.CreateClient().GetAsync("/health/live");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Ready_IsUnhealthy_WithoutBlender()
    {
        using var factory = Factory("/nonexistent/blender", withData: true);
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Ready_IsUnhealthy_WithoutTheData()
    {
        var blender = Path.Combine(_dir, "blender");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blender, "");
        using var factory = Factory(blender, withData: false);
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task Ready_IsHealthy_WithBlenderDataAndAWritableCache()
    {
        var blender = Path.Combine(_dir, "blender");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blender, "");
        using var factory = Factory(blender, withData: true);
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task TheOpenApiDocument_DescribesTheThreeEndpoints()
    {
        using var factory = Factory("/nonexistent/blender", withData: false);
        var client = factory.CreateClient();

        var doc = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var paths = doc.GetProperty("paths");
        Assert.That(paths.TryGetProperty("/spartans", out _), Is.True);
        Assert.That(paths.TryGetProperty("/spartans/{id}", out _), Is.True);
        Assert.That(paths.TryGetProperty("/spartans/{id}/file", out _), Is.True);
        Assert.That((await client.GetAsync("/swagger/index.html")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RendersAreCounted_ByOutcome()
    {
        var counts = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RenderTelemetry.Name) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "spartan_render.jobs") return;
            var outcome = tags.ToArray().Single(t => t.Key == "outcome").Value!.ToString()!;
            lock (counts) counts[outcome] = counts.GetValueOrDefault(outcome) + value;
        });
        listener.Start();

        using var factory = Factory("/nonexistent/blender", withData: false);
        var client = factory.CreateClient();
        var body = new { core = "Mark VII", armor = new Dictionary<string, string>(), coating = "1", output = "png", size = "small" };

        var first = await client.PostAsJsonAsync("/spartans", body);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        for (var i = 0; i < 100 && !File.Exists(Path.Combine(_dir, "cache", $"{id}.png")); i++) await Task.Delay(20);
        await client.PostAsJsonAsync("/spartans", body);

        listener.RecordObservableInstruments();
        lock (counts)
        {
            Assert.That(counts.GetValueOrDefault("done"), Is.EqualTo(1));
            Assert.That(counts.GetValueOrDefault("cached"), Is.EqualTo(1));
        }
    }

    [Test]
    public void ForwardedHeaders_TrustOnlyTheConfiguredNetworks()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnownIPNetworks:0"] = "10.42.0.0/16",
            ["KnownIPNetworks:1"] = "not-a-network",
            ["KnownProxies:0"] = "192.168.1.1",
            ["ForwardLimit"] = "2",
        }).Build();
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersSetup.Apply(options, config);

        Assert.That(options.KnownIPNetworks, Has.Count.EqualTo(1));
        Assert.That(options.KnownProxies.Select(p => p.ToString()), Does.Contain("192.168.1.1"));
        Assert.That(options.ForwardLimit, Is.EqualTo(2));
    }
}
