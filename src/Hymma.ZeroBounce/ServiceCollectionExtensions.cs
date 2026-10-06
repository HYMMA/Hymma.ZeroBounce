using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hymma.ZeroBounce;

/// <summary>
/// Extension methods for registering ZeroBounce services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the ZeroBounce client, binding <see cref="ZeroBounceOptions"/> from the
    /// <c>ZeroBounce</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The root configuration (the <c>ZeroBounce</c> section is read from it).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddZeroBounce(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ZeroBounceOptions>(configuration.GetSection(ZeroBounceOptions.SectionName));
        services.AddHttpClient<IZeroBounceClient, ZeroBounceClient>();
        return services;
    }

    /// <summary>
    /// Adds the ZeroBounce client with a configuration action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure ZeroBounce options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddZeroBounce(this IServiceCollection services, Action<ZeroBounceOptions> configureOptions)
    {
        services.Configure(configureOptions);
        services.AddHttpClient<IZeroBounceClient, ZeroBounceClient>();
        return services;
    }

    /// <summary>
    /// Adds the ZeroBounce client with just an API key.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apiKey">The ZeroBounce API key.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddZeroBounce(this IServiceCollection services, string apiKey)
    {
        return services.AddZeroBounce(options => options.ApiKey = apiKey);
    }
}
