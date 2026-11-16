using Microsoft.Extensions.Logging;

namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>Builds the registry from the configured entities plus, when enabled, what the IPX reports.</summary>
public static class EntityRegistryFactory
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public static EntityRegistry Create(
        EntityMapper mapper, EntitiesOptions entities, IpxOptions options, IpxHttpApi api, ILogger logger)
    {
        var declared = entities.Declared;
        var definitions = declared;
        if (options.AutoDiscover)
        {
            definitions = IpxEntityDiscovery.Merge(declared, DiscoverWithRetry(api, entities.Overrides, logger), logger);
        }

        var mapped = mapper.MapAll(definitions);
        logger.LogInformation("{Declared} declared + {Discovered} auto-discovered entities",
            declared.Count, mapped.Count - declared.Count);
        return new EntityRegistry(mapped);
    }

    // The entity list must be complete before MQTT discovery is published: wait for the IPX instead of
    // publishing a partial device that Home Assistant would then keep.
    private static IReadOnlyList<EntityDefinition> DiscoverWithRetry(IpxHttpApi api, IReadOnlyDictionary<string, EntityOverride> overrides, ILogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var inventory = api.GetInventoryAsync(CancellationToken.None).GetAwaiter().GetResult();
                return IpxEntityDiscovery.Discover(inventory, overrides);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                logger.LogWarning("IPX not reachable for entity auto-discovery (attempt {Attempt}): {Message}. Retrying in {Delay}s",
                    attempt, ex.Message, RetryDelay.TotalSeconds);
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
