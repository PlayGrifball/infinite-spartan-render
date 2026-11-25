using System.Threading.RateLimiting;
using InfiniteSpartanRender;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RenderOptions>(builder.Configuration.GetSection("Render"));
builder.Services.AddSingleton<IBlenderRunner, BlenderRunner>();
builder.Services.AddSingleton<RenderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RenderService>());
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, context.RequestServices.GetRequiredService<IOptions<RenderOptions>>().Value.SubmitsPerMinute),
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();
app.UseRateLimiter();

static bool IsId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

app.MapGet("/health/live", () => Results.Ok());

// Ask for a render or a model; the answer is a job id, because a render takes minutes.
app.MapPost("/spartans", (Loadout loadout, RenderService service, IOptions<RenderOptions> options, HttpResponse response) =>
{
    if (loadout.Validate(options.Value.Cores) is { } problem) return Results.BadRequest(new { error = problem });
    if (service.Submit(loadout) is not { } status)
    {
        response.Headers.RetryAfter = "60";
        return Results.Json(new { error = "The render queue is full; try again later." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Accepted($"/spartans/{status.Id}", new { id = status.Id, state = status.State.ToString().ToLowerInvariant(), output = loadout.Output });
}).RequireRateLimiting("submit");

app.MapGet("/spartans/{id}", (string id, RenderService service) =>
    IsId(id) && service.Status(id) is { } s
        ? Results.Ok(new { id = s.Id, state = s.State.ToString().ToLowerInvariant(), error = s.Error })
        : Results.NotFound(new { error = "No such job. Jobs do not survive a restart: POST the loadout again." }));

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
