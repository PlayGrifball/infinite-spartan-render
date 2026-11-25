using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public enum JobState { Queued, Running, Done, Failed }

public sealed record JobStatus(string Id, JobState State, string? Error, DateTimeOffset At);

public sealed class RenderService(IBlenderRunner runner, IOptions<RenderOptions> options, ILogger<RenderService> logger) : BackgroundService
{
    // What clients see of a failure; Blender's own output goes to the log only.
    public const string FailureMessage = "The render failed; ask again, and report it if it keeps failing.";

    private readonly Channel<(string Id, Loadout Loadout)> _queue = Channel.CreateBounded<(string, Loadout)>(
        new BoundedChannelOptions(Math.Max(1, options.Value.MaxQueued)) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, JobStatus> _jobs = new();

    private string PathFor(string id, string output) => Path.Combine(options.Value.CacheDirectory, $"{id}.{output}");

    public string CachedPath(string id, string output) => PathFor(id, output);

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
        var id = loadout.Id();
        if (File.Exists(PathFor(id, loadout.Output))) return new JobStatus(id, JobState.Done, null, DateTimeOffset.UtcNow);

        var queued = new JobStatus(id, JobState.Queued, null, DateTimeOffset.UtcNow);
        var current = _jobs.AddOrUpdate(id, queued, (_, existing) => existing.State is JobState.Queued or JobState.Running ? existing : queued);
        if (!ReferenceEquals(current, queued)) return current;
        if (_queue.Writer.TryWrite((id, loadout))) return current;
        _jobs.TryRemove(id, out _);
        return null;
    }

    // Drops cache files past CacheDays and finished job records past a day.
    public void Sweep(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Max(1, options.Value.CacheDays)).UtcDateTime;
        if (Directory.Exists(options.Value.CacheDirectory))
            foreach (var file in Directory.EnumerateFiles(options.Value.CacheDirectory))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        foreach (var (id, status) in _jobs)
            if (status.State is JobState.Done or JobState.Failed && status.At < now.AddDays(-1)) _jobs.TryRemove(id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        try
        {
            while (true)
            {
                // Take a slot before taking a job, so waiting jobs stay in the bounded queue.
                await slots.WaitAsync(stoppingToken);
                (string Id, Loadout Loadout) item;
                try { item = await _queue.Reader.ReadAsync(stoppingToken); }
                catch { slots.Release(); throw; }
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try { await RunJob(item.Id, item.Loadout, stoppingToken); }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) { }
        await Task.WhenAll(running);
    }

    private async Task RunJob(string id, Loadout loadout, CancellationToken cancellationToken)
    {
        _jobs[id] = new JobStatus(id, JobState.Running, null, DateTimeOffset.UtcNow);
        var final = PathFor(id, loadout.Output);
        // Ends in the real extension: Blender and its exporters append one when it is missing.
        var partial = PathFor(id + ".partial", loadout.Output);
        try
        {
            await runner.RunAsync(loadout, partial, cancellationToken);
            File.Move(partial, final, overwrite: true);
            _jobs[id] = new JobStatus(id, JobState.Done, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            File.Delete(partial);
            _jobs.TryRemove(id, out _);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Render {Id} failed", id);
            File.Delete(partial);
            _jobs[id] = new JobStatus(id, JobState.Failed, FailureMessage, DateTimeOffset.UtcNow);
        }
    }
}
