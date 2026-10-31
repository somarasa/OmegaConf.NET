using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OmegaConf.NET.Extensions;

/// <summary>
/// Extension methods for integrating OmegaConf.NET with <see cref="IConfigurationBuilder"/>
/// and <see cref="IServiceCollection"/> in .NET Generic Host and ASP.NET Core applications.
/// </summary>
public static class OmegaConfigurationExtensions
{
    /// <summary>
    /// Adds OmegaConf.NET configuration source to the <see cref="IConfigurationBuilder"/>.
    /// </summary>
    public static IConfigurationBuilder AddOmegaConf(this IConfigurationBuilder builder, Action<OmegaConfBuilder> configure)
    {
        var source = new OmegaConfigurationSource { Configure = configure };
        return builder.Add(source);
    }

    /// <summary>
    /// Binds an OmegaConf composed configuration to a strongly typed options instance in the dependency injection container.
    /// </summary>
    public static IServiceCollection ConfigureOmegaConf<T>(this IServiceCollection services, Action<OmegaConfBuilder> configure)
        where T : class
    {
        var builder = OmegaConfBuilder.Create<T>();
        configure(builder.InnerBuilder);
        var instance = builder.Build();
        services.AddSingleton(instance);
        return services;
    }
}
