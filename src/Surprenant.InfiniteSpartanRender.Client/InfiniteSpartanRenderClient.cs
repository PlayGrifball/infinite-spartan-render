using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Surprenant.InfiniteSpartanRender.Client;

/// <summary>The API refused or could not answer: <see cref="StatusCode"/> 400 (invalid loadout), 429 (rate limit), 503 (queue full).</summary>
public sealed class SpartanRenderException(HttpStatusCode statusCode, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>How long the API asked to wait before trying again, when it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Calls the Infinite Spartan Render API. A render takes minutes, so asking returns a job to wait on.</summary>
public sealed class InfiniteSpartanRenderClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Asks for a render; an identical earlier request is answered from the API's cache.</summary>
    public async Task<SpartanJob> SubmitAsync(SpartanLoadout loadout, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("spartans", loadout, Json, cancellationToken);
        return await ReadAsync<SpartanJob>(response, cancellationToken);
    }

    /// <summary>The job's state, or null when the API does not know it (jobs do not survive an API restart: submit again).</summary>
    public async Task<SpartanJob?> GetJobAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"spartans/{Uri.EscapeDataString(id)}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<SpartanJob>(response, cancellationToken);
    }

    /// <summary>Polls until the job is done or failed.</summary>
    public async Task<SpartanJob> WaitAsync(SpartanJob job, TimeSpan? pollEvery = null, CancellationToken cancellationToken = default)
    {
        while (!job.IsDone && !job.IsFailed)
        {
            await Task.Delay(pollEvery ?? TimeSpan.FromSeconds(5), cancellationToken);
            job = await GetJobAsync(job.Id, cancellationToken)
                ?? throw new SpartanRenderException(HttpStatusCode.NotFound, "The API no longer knows this job (it restarted): submit the loadout again.");
        }
        return job;
    }

    /// <summary>The finished PNG or GLB of a done job.</summary>
    public async Task<byte[]> GetFileAsync(string id, CancellationToken cancellationToken = default) =>
        await DownloadAsync($"spartans/{Uri.EscapeDataString(id)}/file", cancellationToken);

    /// <summary>The finished file of a set's item.</summary>
    public async Task<byte[]> GetFileAsync(SpartanSetItem item, CancellationToken cancellationToken = default) =>
        await DownloadAsync(item.Url.TrimStart('/'), cancellationToken);

    /// <summary>Submits, waits and downloads: the file for one loadout.</summary>
    public async Task<byte[]> RenderAsync(SpartanLoadout loadout, TimeSpan? pollEvery = null, CancellationToken cancellationToken = default)
    {
        var job = await WaitAsync(await SubmitAsync(loadout, cancellationToken), pollEvery, cancellationToken);
        if (job.IsFailed) throw new SpartanRenderException(HttpStatusCode.InternalServerError, job.Error ?? "The render failed.");
        return await GetFileAsync(job.Id, cancellationToken);
    }

    /// <summary>Asks for the six files of a Spartan in a stance and in the neutral one.</summary>
    public async Task<SpartanSet> SubmitSetAsync(SpartanSetRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("spartan-sets", request, Json, cancellationToken);
        return await ReadAsync<SpartanSet>(response, cancellationToken);
    }

    /// <summary>The set's state, or null when the API does not know it.</summary>
    public async Task<SpartanSet?> GetSetAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"spartan-sets/{Uri.EscapeDataString(id)}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<SpartanSet>(response, cancellationToken);
    }

    /// <summary>Polls until the whole set is done or failed.</summary>
    public async Task<SpartanSet> WaitAsync(SpartanSet set, TimeSpan? pollEvery = null, CancellationToken cancellationToken = default)
    {
        while (!set.IsDone && !set.IsFailed)
        {
            await Task.Delay(pollEvery ?? TimeSpan.FromSeconds(10), cancellationToken);
            set = await GetSetAsync(set.Id, cancellationToken)
                ?? throw new SpartanRenderException(HttpStatusCode.NotFound, "The API no longer knows this set (it restarted): submit it again.");
        }
        return set;
    }

    private async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new SpartanRenderException(response.StatusCode, "The API answered with an empty body.");
    }

    private static async Task<SpartanRenderException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error)) message = error.GetString();
        }
        catch (JsonException) { }
        var retry = response.Headers.RetryAfter?.Delta;
        return new SpartanRenderException(response.StatusCode, message ?? $"The API answered {(int)response.StatusCode} {response.ReasonPhrase}.", retry);
    }
}
