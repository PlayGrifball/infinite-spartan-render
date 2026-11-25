using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public enum JobState { Queued, Running, Done, Failed }

public sealed record JobStatus(string Id, JobState State, string? Error);

public sealed class RenderService(IBlenderRunner runner, IOptions<RenderOptions> options, ILogger<RenderService> logger) : BackgroundService
{
    private readonly Channel<(string Id, Loadout Loadout)> _queue = Channel.CreateUnbounded<(string, Loadout)>();
    private readonly ConcurrentDictionary<string, JobStatus> _jobs = new();

    private string PathFor(string id, Loadout loadout) => Path.Combine(options.Value.CacheDirectory, $"{id}.{loadout.Output}");

    public string CachedPath(string id, string output)
    {
        var path = Path.Combine(options.Value.CacheDirectory, $"{id}.{output}");
        return path;
    }

    // Done when the file is in the cache (survives restarts); otherwise whatever this process knows about the job.
    public JobStatus? Status(string id)
    {
        foreach (var output in new[] { "png", "glb" })
            if (File.Exists(CachedPath(id, output))) return new JobStatus(id, JobState.Done, null);
        return _jobs.TryGetValue(id, out var status) ? status : null;
    }

    public JobStatus Submit(Loadout loadout)
    {
        var id = loadout.Id();
        if (File.Exists(PathFor(id, loadout))) return new JobStatus(id, JobState.Done, null);

        var queued = new JobStatus(id, JobState.Queued, null);
        var current = _jobs.AddOrUpdate(id, queued, (_, existing) => existing.State is JobState.Queued or JobState.Running ? existing : queued);
        if (ReferenceEquals(current, queued)) _queue.Writer.TryWrite((id, loadout));
        return current;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.Value.CacheDirectory);
        var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentJobs));
        var running = new List<Task>();
        await foreach (var (id, loadout) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await slots.WaitAsync(stoppingToken);
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(async () =>
            {
                try { await RunJob(id, loadout, stoppingToken); }
                finally { slots.Release(); }
            }, stoppingToken));
        }
        await Task.WhenAll(running);
    }

    private async Task RunJob(string id, Loadout loadout, CancellationToken cancellationToken)
    {
        _jobs[id] = new JobStatus(id, JobState.Running, null);
        var final = PathFor(id, loadout);
        var partial = final + ".partial";
        try
        {
            await runner.RunAsync(loadout, partial, cancellationToken);
            File.Move(partial, final, overwrite: true);
            _jobs[id] = new JobStatus(id, JobState.Done, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Render {Id} failed", id);
            File.Delete(partial);
            _jobs[id] = new JobStatus(id, JobState.Failed, e.Message);
        }
    }
}
