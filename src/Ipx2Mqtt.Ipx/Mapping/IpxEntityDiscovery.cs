using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>An io or ana object as listed by <c>/api/core/io|ana</c> (<c>Link1</c> is the owning extension, 0 if none).</summary>
public sealed record IpxListedObject(long Id, string Name, long Link1, bool IsAna);

/// <summary>An extension returned by <c>/api/ebx/&lt;kind&gt;</c>.</summary>
public sealed record IpxExtension(string Kind, long Id, string Name);

public sealed record IpxInventory(IReadOnlyList<IpxListedObject> Objects, IReadOnlyList<IpxExtension> Extensions);

/// <summary>
/// Builds the entity list from what the IPX itself reports, so that every relay, dimmer and analog output is
/// exposed without being declared one by one. The IPX does not return object ids in its extension descriptions;
/// the ids come from the object names, which follow fixed patterns:
/// <list type="bullet">
/// <item><c>[01-IPX]Relay cmd n</c> / <c>Relay state n</c>, <c>[IPX]Open coll. n</c> / <c>Open coll. state n</c>,
/// <c>[03-8R]Relay cmd n</c> / <c>Relay state n</c>: <c>switch</c></item>
/// <item><c>[06-DM]On/Off n</c> + <c>Consign n</c> + <c>Position n</c> (dimmers): a <c>light</c> (on/off + 0-100 level)</item>
/// <item>X-010V, X-PWM, X-400: every ana object linked to the extension becomes a <c>number</c> (not validated
/// on hardware)</item>
/// </list>
/// </summary>
public static partial class IpxEntityDiscovery
{
    public static readonly string[] AnalogOutputKinds = { "x010v", "xpwm", "x400" };

    [GeneratedRegex(@"^\[(?<tag>[^\]]+)\]\s*(?<what>Relay cmd|Relay state|On/Off|Consign|Position|Open coll\.\s+state|Open coll\.)\s*(?<n>\d+|All)?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    public static IReadOnlyList<EntityDefinition> Discover(IpxInventory inventory, IReadOnlyDictionary<string, string>? labels = null)
    {
        string Label(string tag, string n, string fallback) =>
            int.TryParse(n, out var channel) && labels is not null
            && labels.TryGetValue($"{tag}.{channel:00}", out var label) && !string.IsNullOrWhiteSpace(label)
                ? label
                : fallback;

        var result = new List<EntityDefinition>();

        // (tag, what, n) -> object
        var byRole = new Dictionary<(string Tag, string What, string N), IpxListedObject>();
        foreach (var o in inventory.Objects)
        {
            var m = NamePattern().Match(o.Name);
            // Every wanted object is numbered (or "All"): this skips e.g. "[Push N°1]On/Off".
            if (!m.Success || m.Groups["n"].Value.Length == 0)
            {
                continue;
            }

            var what = Regex.Replace(m.Groups["what"].Value, @"\s+", " ").ToLowerInvariant();
            byRole.TryAdd((m.Groups["tag"].Value, what, m.Groups["n"].Value), o);
        }

        foreach (var ((tag, what, n), cmd) in byRole.OrderBy(kv => kv.Value.Id))
        {
            switch (what)
            {
                case "relay cmd":
                case "open coll.":
                    var stateWhat = what == "relay cmd" ? "relay state" : "open coll. state";
                    var state = byRole.GetValueOrDefault((tag, stateWhat, n));
                    var board = tag.EndsWith("IPX", StringComparison.OrdinalIgnoreCase);
                    result.Add(new EntityDefinition
                    {
                        Type = what == "open coll." ? "collector" : board ? "relay" : TypeOf(tag),
                        Index = int.Parse(n),
                        Name = what == "open coll." ? $"Collecteur ouvert {n}" : Label(tag, n, board ? $"Relais {n}" : $"{tag} relais {n}"),
                        Component = "switch",
                        CommandId = cmd.Id,
                        StateId = state?.Id ?? cmd.Id,
                    });
                    break;

                case "on/off":
                    if (n == "All")
                    {
                        result.Add(new EntityDefinition
                        {
                            Type = TypeOf(tag) + "-all",
                            Index = 1,
                            Name = $"{tag} tout",
                            Component = "switch",
                            CommandId = cmd.Id,
                            StateId = cmd.Id,
                        });
                        break;
                    }

                    // A dimmer channel = On/Off io + Consign (requested level) + Position (actual level) ana.
                    result.Add(new EntityDefinition
                    {
                        Type = TypeOf(tag),
                        Index = int.Parse(n),
                        Name = Label(tag, n, $"{tag} variateur {n}"),
                        Component = "light",
                        CommandId = cmd.Id,
                        StateId = cmd.Id,
                        BrightnessCommandId = byRole.GetValueOrDefault((tag, "consign", n))?.Id,
                        BrightnessStateId = byRole.GetValueOrDefault((tag, "position", n))?.Id,
                    });
                    break;
            }
        }

        foreach (var ext in inventory.Extensions.Where(e => AnalogOutputKinds.Contains(e.Kind)))
        {
            var channels = inventory.Objects.Where(o => o.IsAna && o.Link1 == ext.Id).OrderBy(o => o.Id).ToList();
            for (var i = 0; i < channels.Count; i++)
            {
                result.Add(new EntityDefinition
                {
                    Type = ext.Kind,
                    Name = $"{ext.Name} {channels[i].Name}",
                    Component = "number",
                    CommandId = channels[i].Id,
                    StateId = channels[i].Id,
                    Min = 0,
                    Max = 100,
                });
            }
        }

        return result;
    }

    /// <summary>Entities already declared by hand win: discovered ones using the same IPX ids or the same key are dropped.</summary>
    public static IReadOnlyList<EntityDefinition> Merge(
        IReadOnlyList<EntityDefinition> declared, IReadOnlyList<EntityDefinition> discovered, ILogger? logger = null)
    {
        var ids = new HashSet<long>(declared.SelectMany(IdsOf));
        var keys = new HashSet<string>(declared.Where(d => d.Index is not null).Select(d => $"{d.Type}_{d.Index}"));
        var result = new List<EntityDefinition>(declared);

        foreach (var entity in discovered)
        {
            var key = entity.Index is { } i ? $"{entity.Type}_{i}" : null;
            if (IdsOf(entity).Any(ids.Contains) || (key is not null && keys.Contains(key)))
            {
                logger?.LogDebug("Auto-discovered '{Name}' skipped: already declared in Entities", entity.Name);
                continue;
            }

            result.Add(entity);
        }

        return result;
    }

    private static IEnumerable<long> IdsOf(EntityDefinition d) =>
        new[] { d.StateId, d.CommandId, d.BrightnessStateId, d.BrightnessCommandId }.Where(x => x is not null).Select(x => x!.Value);

    // Topic-safe label: "06-DM" -> "06-dm", "03-8R" -> "03-8r".
    private static string TypeOf(string tag) => Regex.Replace(tag.ToLowerInvariant(), "[^a-z0-9_-]", "_");
}
