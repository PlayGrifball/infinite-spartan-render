using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PlayGrifball.InfiniteSpartanRender.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="InfiniteSpartanRenderClient"/> as a typed HttpClient configured from <paramref name="section"/> (BaseAddress).</summary>
    public static IHttpClientBuilder AddInfiniteSpartanRenderClient(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<InfiniteSpartanRenderClientOptions>().Bind(section);
        return AddCore(services);
    }

    /// <summary>Registers <see cref="InfiniteSpartanRenderClient"/> configured in code.</summary>
    public static IHttpClientBuilder AddInfiniteSpartanRenderClient(this IServiceCollection services, Action<InfiniteSpartanRenderClientOptions> configure)
    {
        services.AddOptions<InfiniteSpartanRenderClientOptions>().Configure(configure);
        return AddCore(services);
    }

    private static IHttpClientBuilder AddCore(IServiceCollection services) =>
        services.AddHttpClient<InfiniteSpartanRenderClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<InfiniteSpartanRenderClientOptions>>().Value;
            http.BaseAddress = options.BaseAddress
                ?? throw new InvalidOperationException("Set the Infinite Spartan Render client's BaseAddress.");
            // A file can be a full-size GLB, and waiting is done by polling, so no single call is long.
            http.Timeout = TimeSpan.FromMinutes(5);
        });
}
