using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OmegaConf.NET.Extensions;

public static class OmegaConfigurationExtensions
{
    /// <summary>
    /// Adds OmegaConf.NET configuration source to the ConfigurationBuilder.
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
