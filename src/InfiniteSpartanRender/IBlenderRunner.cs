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
    public string WorkDirectory { get; set; } = "/tmp/render";
    public int MaxConcurrentJobs { get; set; } = 1;
    public int MaxQueued { get; set; } = 20;
    public int SubmitsPerMinute { get; set; } = 60;
    public int CacheDays { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 1800;
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

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Blender did not start");
            var tail = new Queue<string>();
            void Keep(string? line)
            {
                if (line is null) return;
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
            // The redirected streams are read asynchronously: wait for the last lines before reporting them.
            process.WaitForExit();

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
