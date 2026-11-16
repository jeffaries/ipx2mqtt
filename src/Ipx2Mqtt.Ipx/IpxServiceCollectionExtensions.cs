using System.Net.Http;
using Ipx2Mqtt.Ipx.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Ipx;

public static class IpxServiceCollectionExtensions
{
    public static IServiceCollection AddIpx(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IpxOptions>(configuration.GetSection(IpxOptions.SectionName));
        services.Configure<DeviceOptions>(configuration.GetSection(DeviceOptions.SectionName));
        services.Configure<PushOptions>(configuration.GetSection(PushOptions.SectionName));
        services.Configure<EntitiesOptions>(configuration);

        services.AddSingleton<IpxDiagnostics>();

        services.AddHttpClient("ipx")
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new HttpClientHandler();
                if (sp.GetRequiredService<IOptions<IpxOptions>>().Value.AllowInvalidCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                return handler;
            });

        // One shared instance so that the login token is shared between the WebSocket and the polling client.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<IpxHttpApi>(
            sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient("ipx")));
        services.AddSingleton<IpxWebSocketClient>();
        services.AddSingleton<IpxRestPollingClient>();
        services.AddSingleton<IIpxClient, IpxClientStrategy>();
        services.AddSingleton<IpxPushReceiver>();

        services.AddSingleton<IComponentMapper, SwitchMapper>();
        services.AddSingleton<IComponentMapper, BinarySensorMapper>();
        services.AddSingleton<IComponentMapper, SensorMapper>();
        services.AddSingleton<IComponentMapper, LightMapper>();
        services.AddSingleton<IComponentMapper, NumberMapper>();
        services.AddSingleton<EntityMapper>();
        services.AddSingleton(sp => EntityRegistryFactory.Create(
            sp.GetRequiredService<EntityMapper>(),
            sp.GetRequiredService<IOptions<EntitiesOptions>>().Value,
            sp.GetRequiredService<IOptions<IpxOptions>>().Value,
            sp.GetRequiredService<IpxHttpApi>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ipx2Mqtt.Ipx.Mapping.EntityDiscovery")));
        return services;
    }
}
