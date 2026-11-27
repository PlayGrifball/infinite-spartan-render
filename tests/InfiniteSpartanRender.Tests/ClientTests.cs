using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using PlayGrifball.InfiniteSpartanRender.Client;
using ClientSetRequest = PlayGrifball.InfiniteSpartanRender.Client.SpartanSetRequest;

namespace InfiniteSpartanRender.Tests;

public class ClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string? Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static InfiniteSpartanRenderClient ClientFor(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://spartan.test/") });

    [Test]
    public async Task Submit_PostsTheLoadoutAsCamelCaseJson_AndReadsTheJob()
    {
        var stub = new Stub((_, _) => Reply(HttpStatusCode.Accepted, """{"id":"abc","state":"queued","output":"glb"}"""));

        var job = await ClientFor(stub).SubmitAsync(new SpartanLoadout { Core = "Mark VII", Output = "glb", Stance = 7, Armor = { ["helmet"] = "Mk 7" }, Parts = { new SpartanPart(-592012529, 1704860283) } });

        Assert.That(job, Is.EqualTo(new SpartanJob("abc", "queued", "glb")));
        var sent = stub.Requests.Single();
        Assert.That((sent.Method, sent.Path), Is.EqualTo((HttpMethod.Post, "/spartans")));
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.That(body.GetProperty("core").GetString(), Is.EqualTo("Mark VII"));
        Assert.That(body.GetProperty("output").GetString(), Is.EqualTo("glb"));
        Assert.That(body.GetProperty("stance").GetInt32(), Is.EqualTo(7));
        Assert.That(body.GetProperty("armor").GetProperty("helmet").GetString(), Is.EqualTo("Mk 7"));
        var part = body.GetProperty("parts").EnumerateArray().Single();
        Assert.That((part.GetProperty("region").GetInt32(), part.GetProperty("permutation").GetInt32()), Is.EqualTo((-592012529, 1704860283)));
    }

    [Test]
    public async Task GetCatalog_ReadsCoresPiecesCoatingsVisorsWeaponsAndPoses()
    {
        var stub = new Stub((_, _) => Reply(HttpStatusCode.OK, """{"cores":[{"name":"Mark VII","regions":[{"kind":"KIT","key":"helmet","region":-5,"pieces":[{"permutation":7,"name":null}]}]}],"coatings":["-1"],"visors":["V"],"pendingCores":["Yoroi"],"weapons":[{"name":"VK78 Commando Rifle","coreType":"WeaponCommando","tags":[-40348340],"coatings":["5"]}],"poses":[{"name":"Captain's Pride","stance":11,"weapon":"Energy Sword"}]}"""));

        var catalog = await ClientFor(stub).GetCatalogAsync();

        var region = catalog.Cores.Single().Regions.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stub.Requests.Single().Path, Is.EqualTo("/catalog"));
            Assert.That((region.Kind, region.Key, region.Region, region.Pieces.Single()), Is.EqualTo(("KIT", "helmet", -5, new SpartanCatalogPiece(7, null))));
            Assert.That((catalog.Coatings, catalog.Visors, catalog.PendingCores), Is.EqualTo((new[] { "-1" }, new[] { "V" }, new[] { "Yoroi" })));
            var weapon = catalog.Weapons!.Single();
            Assert.That((weapon.Name, weapon.CoreType, weapon.Tags.Single(), weapon.Coatings.Single()), Is.EqualTo(("VK78 Commando Rifle", "WeaponCommando", -40348340, "5")));
            Assert.That(catalog.Poses!.Single(), Is.EqualTo(new SpartanCatalogPose("Captain's Pride", 11, "Energy Sword")));
        }
    }

    [Test]
    public void ABadLoadout_ThrowsWithTheApisMessage()
    {
        var stub = new Stub((_, _) => Reply(HttpStatusCode.BadRequest, """{"error":"core must be one of: Mark VII"}"""));

        var e = Assert.ThrowsAsync<SpartanRenderException>(() => ClientFor(stub).SubmitAsync(new SpartanLoadout { Core = "Nope" }))!;

        Assert.That(e.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(e.Message, Is.EqualTo("core must be one of: Mark VII"));
    }

    [Test]
    public void AFullQueue_ThrowsWithTheRetryAfter()
    {
        var stub = new Stub((_, _) =>
        {
            var r = Reply(HttpStatusCode.ServiceUnavailable, """{"error":"The render queue is full; try again later."}""");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return r;
        });

        var e = Assert.ThrowsAsync<SpartanRenderException>(() => ClientFor(stub).SubmitAsync(new SpartanLoadout { Core = "Mark VII" }))!;

        Assert.That(e.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(e.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public async Task AnUnknownJob_IsNull()
    {
        var stub = new Stub((_, _) => Reply(HttpStatusCode.NotFound, """{"error":"No such job."}"""));

        Assert.That(await ClientFor(stub).GetJobAsync("gone"), Is.Null);
    }

    [Test]
    public async Task Wait_PollsUntilTheJobIsDone()
    {
        var states = new Queue<string>(["running", "running", "done"]);
        var stub = new Stub((_, _) => Reply(HttpStatusCode.OK, $$"""{"id":"abc","state":"{{states.Dequeue()}}"}"""));

        var job = await ClientFor(stub).WaitAsync(new SpartanJob("abc", "queued"), TimeSpan.FromMilliseconds(1));

        Assert.That(job.IsDone, Is.True);
        Assert.That(stub.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    public void Wait_ThrowsWhenTheApiForgotTheJob()
    {
        var stub = new Stub((_, _) => Reply(HttpStatusCode.NotFound, """{"error":"No such job."}"""));

        var e = Assert.ThrowsAsync<SpartanRenderException>(() => ClientFor(stub).WaitAsync(new SpartanJob("abc", "queued"), TimeSpan.FromMilliseconds(1)))!;

        Assert.That(e.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Render_SubmitsWaitsAndDownloadsTheFile()
    {
        var stub = new Stub((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/spartans" => Reply(HttpStatusCode.Accepted, """{"id":"abc","state":"queued","output":"png"}"""),
            "/spartans/abc" => Reply(HttpStatusCode.OK, """{"id":"abc","state":"done"}"""),
            "/spartans/abc/file" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var file = await ClientFor(stub).RenderAsync(new SpartanLoadout { Core = "Mark VII" }, TimeSpan.FromMilliseconds(1));

        Assert.That(file, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void Render_ThrowsWhenTheJobFails()
    {
        var stub = new Stub((r, _) => r.Method == HttpMethod.Post
            ? Reply(HttpStatusCode.Accepted, """{"id":"abc","state":"queued","output":"png"}""")
            : Reply(HttpStatusCode.OK, """{"id":"abc","state":"failed","error":"The render failed; ask again."}"""));

        var e = Assert.ThrowsAsync<SpartanRenderException>(() => ClientFor(stub).RenderAsync(new SpartanLoadout { Core = "Mark VII" }, TimeSpan.FromMilliseconds(1)))!;

        Assert.That(e.Message, Is.EqualTo("The render failed; ask again."));
    }

    [Test]
    public async Task ASet_IsSubmittedPolledAndItsItemsDownloadedByUrl()
    {
        const string items = """[{"name":"glb","state":"done","output":"glb","url":"/spartans/f1/file"}]""";
        var stub = new Stub((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/spartan-sets" => Reply(HttpStatusCode.Accepted, $$"""{"id":"s1","state":"running","items":{{items}}}"""),
            "/spartan-sets/s1" => Reply(HttpStatusCode.OK, $$"""{"id":"s1","state":"done","items":{{items}}}"""),
            "/spartans/f1/file" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var client = ClientFor(stub);

        var set = await client.WaitAsync(await client.SubmitSetAsync(new ClientSetRequest { Core = "Mark VII", Stance = 39388885 }), TimeSpan.FromMilliseconds(1));

        Assert.That(set.IsDone, Is.True);
        Assert.That(await client.GetFileAsync(set.Items[0]), Is.EqualTo(new byte[] { 9 }));
        Assert.That(JsonDocument.Parse(stub.Requests[0].Body!).RootElement.GetProperty("stance").GetInt32(), Is.EqualTo(39388885));
    }

    [Test]
    public async Task TheServiceCollectionExtension_BuildsAClientAtTheConfiguredAddress()
    {
        var services = new ServiceCollection();
        services.AddInfiniteSpartanRenderClient(o => o.BaseAddress = new Uri("http://spartan.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => new Stub((_, _) => Reply(HttpStatusCode.NotFound, "{}")));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<InfiniteSpartanRenderClient>();

        Assert.That(await client.GetJobAsync("x"), Is.Null);
    }
}
