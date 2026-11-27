using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public enum JobState { Queued, Running, Done, Failed }

public sealed record JobStatus(string Id, JobState State, string? Error, DateTimeOffset At);

// A job waiting for a slot, with the request that asked for it: its span is the render span's parent, and Caller is the
// name of the API key the request carried (null: none), so the render shows in the caller's trace and metrics.
public sealed record QueuedJob(string Id, Loadout Loadout, ActivityContext Parent, string? Caller);

public sealed class RenderService(IBlenderRunner runner, IOptions<RenderOptions> options, CatalogService catalog, ILogger<RenderService> logger) : BackgroundService
{
    // What clients see of a failure; Blender's own output goes to the log only.
    public const string FailureMessage = "The render failed; ask again, and report it if it keeps failing.";

    private readonly Channel<QueuedJob> _queue = Channel.CreateBounded<QueuedJob>(
        new BoundedChannelOptions(Math.Max(1, options.Value.MaxQueued)) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, JobStatus> _jobs = new();

    public int QueueDepth => _queue.Reader.Count;

    private string PathFor(string id, string output) => Path.Combine(options.Value.CacheDirectory, $"{id}.{output}");

    public string CachedPath(string id, string output) => PathFor(id, output);

    // The Blender scripts are the renderer: any change to them changes every id, so old files are never served.
    private readonly Lazy<string> _renderer = new(() =>
    {
        var script = options.Value.ScriptPath;
        if (!File.Exists(script)) return "";
        var files = new[] { script, Path.Combine(Path.GetDirectoryName(script) ?? "", "rig.py") }.Where(File.Exists);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(files.SelectMany(File.ReadAllBytes).ToArray()))[..12];
    });

    public Catalog Catalog() => catalog.Get(_renderer.Value);

    // A held weapon's own entry joins the renderer, so a changed entry renders the loadouts holding it again, and no other
    private string IdOf(Loadout loadout)
        => loadout.Id(string.IsNullOrEmpty(loadout.Weapon) ? _renderer.Value : $"{_renderer.Value}|{catalog.WeaponIdentity(loadout.Weapon)}");

    // Done when the file is in the cache (survives restarts); otherwise whatever this process knows about the job.
    // A job lost to a restart is unknown here: the client posts the loadout again.
    public JobStatus? Status(string id)
    {
        foreach (var output in new[] { "png", "glb" })
            if (File.Exists(PathFor(id, output))) return new JobStatus(id, JobState.Done, null, DateTimeOffset.UtcNow);
        return _jobs.TryGetValue(id, out var status) ? status : null;
    }

    // Null when the queue is full.
    public JobStatus? Submit(Loadout loadout)
    {
        var id = IdOf(loadout);
        if (File.Exists(PathFor(id, loadout.Output)))
        {
            RenderTelemetry.Jobs.Add(1, new KeyValuePair<string, object?>("outcome", "cached"), Caller(Activity.Current?.GetTagItem(CallerKeys.SpanTag) as string));
            return new JobStatus(id, JobState.Done, null, DateTimeOffset.UtcNow);
        }

        var queued = new JobStatus(id, JobState.Queued, null, DateTimeOffset.UtcNow);
        var caller = Activity.Current?.GetTagItem(CallerKeys.SpanTag) as string;
        var current = _jobs.AddOrUpdate(id, queued, (_, existing) => existing.State is JobState.Queued or JobState.Running ? existing : queued);
        if (!ReferenceEquals(current, queued))
        {
            RenderTelemetry.Jobs.Add(1, new KeyValuePair<string, object?>("outcome", "joined"), Caller(caller));
            return current;
        }
        if (_queue.Writer.TryWrite(new QueuedJob(id, loadout, Activity.Current?.Context ?? default, caller))) return current;
        _jobs.TryRemove(id, out _);
        RenderTelemetry.Jobs.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"), Caller(caller));
        return null;
    }

    private readonly ConcurrentDictionary<string, SpartanSetRequest> _sets = new();

    // Null when the queue cannot take the whole set; what was queued still renders.
    public SpartanSetStatus? SubmitSet(SpartanSetRequest request)
    {
        var id = request.Id(_renderer.Value);
        foreach (var (_, loadout) in request.Items())
            if (Submit(loadout) is null) return null;
        _sets[id] = request;
        return SetStatus(id);
    }

    public SpartanSetStatus? SetStatus(string id)
    {
        if (!_sets.TryGetValue(id, out var request)) return null;
        var items = request.Items()
            .Select(i => (i.Name, i.Loadout, Id: IdOf(i.Loadout)))
            .Select(i => new SpartanSetItem(i.Name, i.Id, i.Loadout.Output,
                Status(i.Id) ?? new JobStatus(i.Id, JobState.Failed, FailureMessage, DateTimeOffset.UtcNow)))
            .ToList();
        var state = items.Any(i => i.Status.State == JobState.Failed) ? JobState.Failed
            : items.All(i => i.Status.State == JobState.Done) ? JobState.Done
            : items.Any(i => i.Status.State != JobState.Queued) ? JobState.Running
            : JobState.Queued;
        return new SpartanSetStatus(id, state.ToString().ToLowerInvariant(), items);
    }

    // Drops cache files past CacheDays and finished job records past a day.
    public void Sweep(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Max(1, options.Value.CacheDays)).UtcDateTime;
        if (Directory.Exists(options.Value.CacheDirectory))
            foreach (var file in Directory.EnumerateFiles(options.Value.CacheDirectory, "*", SearchOption.AllDirectories))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        foreach (var (id, status) in _jobs)
            if (status.State is JobState.Done or JobState.Failed && status.At < now.AddDays(-1)) _jobs.TryRemove(id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RenderTelemetry.Meter.CreateObservableGauge("spartan_render.queue_depth", () => QueueDepth, description: "Renders waiting for a slot");
        Directory.CreateDirectory(options.Value.CacheDirectory);
        var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentJobs));
        var running = new List<Task>();
        using var sweeper = new PeriodicTimer(TimeSpan.FromHours(1));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await sweeper.WaitForNextTickAsync(stoppingToken)) Sweep(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { }
        }, stoppingToken);

        if (options.Value.WarmImports) _ = Task.Run(() => WarmImports(slots, stoppingToken), CancellationToken.None);

        try
        {
            while (true)
            {
                // Wait for a job, then take a slot before taking it, so waiting jobs stay in the bounded queue and an idle service holds no slot (the warm-up needs one).
                if (!await _queue.Reader.WaitToReadAsync(stoppingToken)) break;
                await slots.WaitAsync(stoppingToken);
                if (!_queue.Reader.TryRead(out var item)) { slots.Release(); continue; }
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try { await RunJob(item, stoppingToken); }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) { }
        await Task.WhenAll(running);
    }

    // Opens each core once so its imported scene is saved before a request needs it; one core at a time, holding a
    // slot like any job. Daily, so the files are touched and the sweep never drops a core in use.
    private async Task WarmImports(SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            using var daily = new PeriodicTimer(TimeSpan.FromDays(1));
            do
            {
                foreach (var core in options.Value.Cores)
                {
                    await slots.WaitAsync(cancellationToken);
                    var marker = PathFor("warm-" + Guid.NewGuid().ToString("N") + ".partial", "warm");
                    try
                    {
                        await runner.RunAsync(new Loadout { Core = core, Output = "warm" }, marker, cancellationToken);
                        await WriteCatalog(core, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { logger.LogError(e, "Warming the import of {Core} failed", core); }
                    finally { File.Delete(marker); slots.Release(); }
                }
            }
            while (await daily.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) { }
    }

    // The core's pieces, read from its saved import (quick once the import exists), kept for GET /catalog. Made again with every warm: an updated Ekur or Blender may have changed them.
    private async Task WriteCatalog(string core, CancellationToken cancellationToken)
    {
        var final = catalog.PathFor(core, _renderer.Value);
        var partial = final + ".partial.json";
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        try
        {
            await runner.RunAsync(new Loadout { Core = core, Output = "catalog" }, partial, cancellationToken);
            File.Move(partial, final, overwrite: true);
        }
        finally { File.Delete(partial); }
    }

    private static KeyValuePair<string, object?> Caller(string? caller) => new("caller", caller ?? "none");

    private async Task RunJob(QueuedJob job, CancellationToken cancellationToken)
    {
        var (id, loadout, parent, caller) = job;
        _jobs[id] = new JobStatus(id, JobState.Running, null, DateTimeOffset.UtcNow);
        var final = PathFor(id, loadout.Output);
        // Ends in the real extension: Blender and its exporters append one when it is missing.
        var partial = PathFor(id + ".partial", loadout.Output);
        using var activity = RenderTelemetry.Source.StartActivity("render", ActivityKind.Internal, parent);
        if (caller is not null) activity?.SetTag(CallerKeys.SpanTag, caller);
        activity?.SetTag("render.id", id);
        activity?.SetTag("render.core", loadout.Core);
        activity?.SetTag("render.output", loadout.Output);
        activity?.SetTag("render.size", loadout.Size);
        var started = Stopwatch.GetTimestamp();
        void Record(string outcome)
        {
            RenderTelemetry.Jobs.Add(1, new KeyValuePair<string, object?>("outcome", outcome), Caller(caller));
            RenderTelemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome), Caller(caller));
            activity?.SetTag("render.outcome", outcome);
        }
        try
        {
            await runner.RunAsync(loadout, partial, cancellationToken);
            File.Move(partial, final, overwrite: true);
            Record("done");
            _jobs[id] = new JobStatus(id, JobState.Done, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            File.Delete(partial);
            _jobs.TryRemove(id, out _);
            Record("cancelled");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Render {Id} failed", id);
            activity?.SetStatus(ActivityStatusCode.Error);
            Record("failed");
            File.Delete(partial);
            _jobs[id] = new JobStatus(id, JobState.Failed, FailureMessage, DateTimeOffset.UtcNow);
        }
    }
}
