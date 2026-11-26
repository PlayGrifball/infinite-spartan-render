namespace Surprenant.InfiniteSpartanRender.Client;

/// <summary>Settings for <see cref="InfiniteSpartanRenderClient"/> (e.g. section <c>SpartanRender</c>).</summary>
public sealed class InfiniteSpartanRenderClientOptions
{
    /// <summary>Where the API is, e.g. <c>http://infinite-spartan-render.ekur-data.svc:8080/</c>.</summary>
    public Uri? BaseAddress { get; set; }
}
