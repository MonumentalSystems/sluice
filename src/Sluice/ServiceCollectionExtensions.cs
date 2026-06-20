using Sluice.Barkd;
using Microsoft.Extensions.DependencyInjection;

namespace Sluice;

/// <summary>DI registration helpers for the Sluice payment primitives.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the barkd REST client: binds <see cref="BarkdClientOptions"/>, the named
    /// <c>"barkd"</c> <see cref="System.Net.Http.HttpClient"/>, and <see cref="IBarkdClient"/> →
    /// <see cref="BarkdClient"/>.</summary>
    public static IServiceCollection AddBarkdClient(this IServiceCollection services, Action<BarkdClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.AddHttpClient("barkd");
        services.AddSingleton<IBarkdClient, BarkdClient>();
        return services;
    }
}
