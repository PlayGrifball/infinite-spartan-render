using System.Threading.RateLimiting;
using InfiniteSpartanRender;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RenderOptions>(builder.Configuration.GetSection("Render"));
builder.Services.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Apply(o, builder.Configuration.GetSection("ForwardedHeaders")));
builder.Services.AddSingleton<IBlenderRunner, BlenderRunner>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<RenderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RenderService>());
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("render", tags: ["ready"]);
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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Infinite Spartan Render", Version = "v1" });
    options.SupportNonNullableReferenceTypes();
    options.CustomSchemaIds(type => type.FullName);
});

// Traces (requests minus health probes, one span per render) and metrics (ASP.NET Core, runtime, the render counters)
// go out over OTLP only: the standard OTEL_* variables configure it, on when OTEL_EXPORTER_OTLP_ENDPOINT is set.
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "infinite-spartan-render"))
    .WithTracing(t => t
        .AddSource(RenderTelemetry.Name)
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")))
    .WithMetrics(m => m
        .AddMeter(RenderTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("v1/swagger.json", "Infinite Spartan Render"));

static bool IsId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

// /health/live runs no checks; /health/ready and /health check that a render could start. Status words only.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapHealthChecks("/health");

// Ask for a render or a model; the answer is a job id, because a render takes minutes.
app.MapPost("/spartans", (Loadout loadout, RenderService service, IOptions<RenderOptions> options, HttpResponse response) =>
{
    if (loadout.Validate(options.Value.Cores) is { } problem) return Results.BadRequest(new ErrorResponse(problem));
    if (service.Submit(loadout) is not { } status)
    {
        response.Headers.RetryAfter = "60";
        return Results.Json(new ErrorResponse("The render queue is full; try again later."), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Accepted($"/spartans/{status.Id}", new JobResponse(status.Id, status.State.ToString().ToLowerInvariant(), loadout.Output));
})
    .RequireRateLimiting("submit")
    .WithName("SubmitSpartan")
    .WithSummary("Ask for a render (png) or a model (glb) of a Spartan loadout")
    .Produces<JobResponse>(StatusCodes.Status202Accepted)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

// What a loadout may name. A core still importing is in pendingCores and joins when its import is done.
app.MapGet("/catalog", (RenderService service) => Results.Ok(service.Catalog()));

app.MapGet("/spartans/{id}", (string id, RenderService service) =>
    IsId(id) && service.Status(id) is { } s
        ? Results.Ok(new JobStatusResponse(s.Id, s.State.ToString().ToLowerInvariant(), s.Error))
        : Results.NotFound(new ErrorResponse("No such job. Jobs do not survive a restart: POST the loadout again.")))
    .WithName("GetSpartanJob")
    .WithSummary("The state of a job: queued, running, done or failed")
    .Produces<JobStatusResponse>()
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

app.MapGet("/spartans/{id}/file", (string id, RenderService service) =>
{
    if (!IsId(id) || service.Status(id) is not { State: JobState.Done }) return Results.NotFound();
    foreach (var (output, type) in new[] { ("png", "image/png"), ("glb", "model/gltf-binary") })
    {
        var path = service.CachedPath(id, output);
        if (File.Exists(path)) return Results.File(path, type, $"spartan-{id}.{output}");
    }
    return Results.NotFound();
})
    .WithName("GetSpartanFile")
    .WithSummary("The finished PNG or GLB")
    .Produces(StatusCodes.Status200OK, contentType: "image/png", additionalContentTypes: "model/gltf-binary")
    .Produces(StatusCodes.Status404NotFound);

// One Spartan in a chosen stance and in the neutral one: a GLB, a body PNG and a headshot PNG of each.
app.MapPost("/spartan-sets", (SpartanSetRequest request, RenderService service, IOptions<RenderOptions> options, HttpResponse response) =>
{
    if (request.Validate(options.Value.Cores) is { } problem) return Results.BadRequest(new ErrorResponse(problem));
    if (service.SubmitSet(request) is not { } set)
    {
        response.Headers.RetryAfter = "60";
        return Results.Json(new ErrorResponse("The render queue is full; try again later."), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Accepted($"/spartan-sets/{set.Id}", ToResponse(set));
})
    .RequireRateLimiting("submit")
    .WithName("SubmitSpartanSet")
    .WithSummary("Ask for a Spartan in a chosen stance and in the neutral one: a GLB, a body PNG and a headshot PNG of each")
    .Produces<SetResponse>(StatusCodes.Status202Accepted)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/spartan-sets/{id}", (string id, RenderService service) =>
    IsId(id) && service.SetStatus(id) is { } set
        ? Results.Ok(ToResponse(set))
        : Results.NotFound(new ErrorResponse("No such set. Sets do not survive a restart: POST the request again.")))
    .WithName("GetSpartanSet")
    .WithSummary("The state of a set and the url of each of its six files")
    .Produces<SetResponse>()
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

static SetResponse ToResponse(SpartanSetStatus set) => new(set.Id, set.State,
    set.Items.Select(i => new SetItemResponse(i.Name, i.Status.State.ToString().ToLowerInvariant(), i.Output, $"/spartans/{i.Id}/file")).ToList());

app.Run();

public partial class Program;
