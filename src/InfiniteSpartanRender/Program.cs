using InfiniteSpartanRender;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RenderOptions>(builder.Configuration.GetSection("Render"));
builder.Services.AddSingleton<IBlenderRunner, BlenderRunner>();
builder.Services.AddSingleton<RenderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RenderService>());

var app = builder.Build();

static bool IsId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

app.MapGet("/health/live", () => Results.Ok());

// Ask for a render or a model; the answer is a job id, because a render takes minutes.
app.MapPost("/spartans", (Loadout loadout, RenderService service) =>
{
    if (loadout.Validate() is { } problem) return Results.BadRequest(new { error = problem });
    var status = service.Submit(loadout);
    return Results.Accepted($"/spartans/{status.Id}", new { id = status.Id, state = status.State.ToString().ToLowerInvariant(), output = loadout.Output });
});

app.MapGet("/spartans/{id}", (string id, RenderService service) =>
    IsId(id) && service.Status(id) is { } s
        ? Results.Ok(new { id = s.Id, state = s.State.ToString().ToLowerInvariant(), error = s.Error })
        : Results.NotFound());

app.MapGet("/spartans/{id}/file", (string id, RenderService service) =>
{
    if (!IsId(id) || service.Status(id) is not { State: JobState.Done }) return Results.NotFound();
    foreach (var (output, type) in new[] { ("png", "image/png"), ("glb", "model/gltf-binary") })
    {
        var path = service.CachedPath(id, output);
        if (File.Exists(path)) return Results.File(path, type, $"spartan-{id}.{output}");
    }
    return Results.NotFound();
});

app.Run();

public partial class Program;
