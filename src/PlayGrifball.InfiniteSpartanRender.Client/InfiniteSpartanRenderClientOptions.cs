namespace PlayGrifball.InfiniteSpartanRender.Client;

/// <summary>Settings for <see cref="InfiniteSpartanRenderClient"/> (e.g. section <c>SpartanRender</c>).</summary>
public sealed class InfiniteSpartanRenderClientOptions
{
    /// <summary>Where the API is, e.g. <c>http://infinite-spartan-render.ekur-data.svc:8080/</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The caller's API key, sent as <c>X-Api-Key</c>. The API names the caller by it in its telemetry; unset, it sends none.</summary>
    public string? ApiKey { get; set; }
}
