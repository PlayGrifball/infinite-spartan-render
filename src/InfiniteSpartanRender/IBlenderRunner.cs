using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InfiniteSpartanRender;

public interface IBlenderRunner
{
    // Writes the render (or model) for the loadout to outputPath, or throws with Blender's last output.
    Task RunAsync(Loadout loadout, string outputPath, CancellationToken cancellationToken);
}

public sealed class RenderOptions
{
    public string BlenderPath { get; set; } = "/opt/blender/blender";
    public string ScriptPath { get; set; } = "/app/blender/spartan.py";
    public string CacheDirectory { get; set; } = "/cache";
    public string DataDirectory { get; set; } = "/data";
    public string EkurAssets { get; set; } = "/opt/ekur/assets";
    // The weapons a stance can hold: name, model file, Halo weapon core type, the action pose weaponTagIds that mean it, its stylelists.
    public string WeaponsPath { get; set; } = "/app/blender/weapons.json";
    // The poses an all-poses model carries: title, stance id and the weaponTagId each holds.
    public string PosesPath { get; set; } = "/app/blender/poses.json";
    public string WorkDirectory { get; set; } = "/tmp/render";
    public int MaxConcurrentJobs { get; set; } = 1;
    public int MaxQueued { get; set; } = 20;
    // Callers that send their key in X-Api-Key are named in the telemetry; a key that matches none is refused (401), no key is allowed.
    public List<CallerKey> ApiKeys { get; set; } = [];
    public int SubmitsPerMinute { get; set; } = 60;
    public int CacheDays { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 1800;
    // Import every core in the background at startup and daily, so no request waits for a first import.
    public bool WarmImports { get; set; } = true;
    public string[] Cores { get; set; } = ["Mark VII", "Mark V [B]", "Yoroi", "Eaglestrike", "Rakshasa", "Chimera", "Mirage IIC", "HAZMAT", "Mark IV"];
}

public sealed class BlenderRunner(IOptions<RenderOptions> options) : IBlenderRunner
{
    public async Task RunAsync(Loadout loadout, string outputPath, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var work = Path.Combine(o.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var loadoutPath = Path.Combine(work, "loadout.json");
            await File.WriteAllTextAsync(loadoutPath, JsonSerializer.Serialize(loadout), cancellationToken);

            var start = new ProcessStartInfo(o.BlenderPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-b", "--factory-startup", "--python", o.ScriptPath, "--", loadoutPath, outputPath })
                start.ArgumentList.Add(arg);
            start.Environment["HOME"] = work;
            start.Environment["ISR_DATA"] = o.DataDirectory;
            start.Environment["ISR_WEAPONS"] = o.WeaponsPath;
            start.Environment["ISR_POSES"] = o.PosesPath;
            start.Environment["ISR_IMPORT_CACHE"] = Path.Combine(o.CacheDirectory, "imports");
            start.Environment["ISR_BAKE_CACHE"] = Path.Combine(o.CacheDirectory, "bakes");

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Blender did not start");
            var tail = new Queue<string>();
            var span = Activity.Current;
            void Keep(string? line)
            {
                if (line is null) return;
                if (PhaseTiming.TryParse(line, out var phase, out var seconds))
                {
                    RenderTelemetry.PhaseDuration.Record(seconds, new KeyValuePair<string, object?>("phase", phase), new KeyValuePair<string, object?>("output", loadout.Output));
                    span?.AddEvent(new ActivityEvent($"phase {phase}", tags: new ActivityTagsCollection { ["seconds"] = seconds }));
                    return;
                }
                lock (tail) { tail.Enqueue(line); if (tail.Count > 30) tail.Dequeue(); }
            }
            process.OutputDataReceived += (_, e) => Keep(e.Data);
            process.ErrorDataReceived += (_, e) => Keep(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException($"Blender did not finish in {o.TimeoutSeconds} seconds");
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string text;
                lock (tail) text = string.Join('\n', tail);
                throw new InvalidOperationException($"Blender exited with {process.ExitCode}: {text}");
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
