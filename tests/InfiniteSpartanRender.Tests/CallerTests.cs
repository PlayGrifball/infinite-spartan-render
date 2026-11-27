using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PlayGrifball.InfiniteSpartanRender.Client;

namespace InfiniteSpartanRender.Tests;

public class CallerTests
{
    private sealed class QuickRunner : IBlenderRunner
    {
        public Task RunAsync(Loadout loadout, string outputPath, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(outputPath, "x", cancellationToken);
    }

    private string _cache = "";
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Path.Combine(Path.GetTempPath(), "isr-" + Guid.NewGuid().ToString("N"));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Render:CacheDirectory", _cache);
            b.UseSetting("Render:WarmImports", "false");
            b.UseSetting("Render:ApiKeys:0:Name", "grif-test");
            b.UseSetting("Render:ApiKeys:0:Key", "test-secret");
            b.UseSetting("Render:ApiKeys:1:Name", "grif-prod");
            b.UseSetting("Render:ApiKeys:1:Key", "prod-secret");
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IBlenderRunner>(new QuickRunner())));
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

    private static object Body(string coating = "1") =>
        new { core = "Mark VII", armor = new Dictionary<string, string> { ["helmet"] = "Mk 7" }, coating, output = "png", size = "small" };

    private async Task<HttpResponseMessage> Post(string? key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/spartans") { Content = JsonContent.Create(body ?? Body()) };
        if (key is not null) request.Headers.Add(CallerKeys.Header, key);
        return await _client.SendAsync(request);
    }

    private async Task WaitDone(HttpResponseMessage accepted)
    {
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        for (var i = 0; i < 100; i++)
        {
            var state = (await _client.GetFromJsonAsync<JsonElement>($"/spartans/{id}")).GetProperty("state").GetString();
            if (state == "done") return;
            await Task.Delay(50);
        }
        Assert.Fail("render never finished");
    }

    [Test]
    public async Task AKnownKey_IsAccepted()
    {
        var test = await Post("test-secret");
        var prod = await Post("prod-secret", Body("2"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(prod.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        }
    }

    [Test]
    public async Task AWrongKey_Is401()
    {
        Assert.That((await Post("nope")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task NoKey_IsStillAllowed_SoUnconfiguredCallersKeepWorking()
    {
        Assert.That((await Post(null)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task Health_IgnoresAWrongKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(CallerKeys.Header, "nope");

        Assert.That((await _client.SendAsync(request)).StatusCode, Is.Not.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TheRenderSpan_NamesTheCaller_AndIsParentedToTheRequest()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RenderTelemetry.Name || s.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        await WaitDone(await Post("test-secret"));

        Activity render;
        Activity request;
        lock (spans)
        {
            render = spans.Single(a => a.OperationName == "render");
            request = spans.Single(a => a.Source.Name == "Microsoft.AspNetCore" && a.TraceId == render.TraceId && a.GetTagItem(CallerKeys.SpanTag) is not null);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(render.GetTagItem(CallerKeys.SpanTag), Is.EqualTo("grif-test"));
            Assert.That(request.GetTagItem(CallerKeys.SpanTag), Is.EqualTo("grif-test"));
            Assert.That(render.ParentSpanId, Is.EqualTo(request.SpanId));
        }
    }

    [Test]
    public async Task TheJobAndDurationMetrics_CarryTheCaller()
    {
        var callers = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == RenderTelemetry.Name && (i.Name == "spartan_render.duration" || i.Name == "spartan_render.jobs")) l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Record(tags.ToArray()));
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(tags.ToArray()));
        void Record(KeyValuePair<string, object?>[] tags)
        {
            var caller = tags.SingleOrDefault(t => t.Key == "caller").Value?.ToString();
            if (caller is not null) lock (callers) callers.Add(caller);
        }
        listener.Start();

        await WaitDone(await Post("prod-secret"));

        lock (callers) Assert.That(callers, Is.Not.Empty.And.All.EqualTo("grif-prod"));
    }

    [Test]
    public async Task TheClient_SendsItsApiKey()
    {
        string? seen = null;
        var services = new ServiceCollection();
        services.AddInfiniteSpartanRenderClient(o => { o.BaseAddress = new Uri("http://spartan.test/"); o.ApiKey = " k1 "; })
            .ConfigurePrimaryHttpMessageHandler(() => new CaptureHandler(r => seen = r.Headers.GetValues(CallerKeys.Header).Single()));
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<InfiniteSpartanRenderClient>().GetJobAsync("x");

        Assert.That(seen, Is.EqualTo("k1"));
    }

    private sealed class CaptureHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }
}
