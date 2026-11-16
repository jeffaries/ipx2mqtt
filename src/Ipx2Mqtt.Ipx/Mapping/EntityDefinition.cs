namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>One exposed entity, as written in the "Entities" configuration list.</summary>
public sealed class EntityDefinition
{
    /// <summary>
    /// When set, this entry does not declare a new entity: it customises an auto-discovered channel, identified by
    /// its module code ("04-8R.05") or the numeric id of its command object ("65536"). Only Name, Component and
    /// Dimmable are used.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>light only (with Key): true = on/off + brightness (dimmers only), false = on/off light. Default true.</summary>
    public bool? Dimmable { get; set; }

    /// <summary>Kind label used in topics: relay, input, analog, dimmer, ... (letters, digits, '_' and '-').</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Index used in topics (ipx800/&lt;type&gt;/&lt;index&gt;/state). Defaults to the state (or command) id.</summary>
    public int? Index { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Home Assistant platform: switch, binary_sensor, sensor, light, ...</summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>IPX object id holding the state (io id or ana id).</summary>
    public long? StateId { get; set; }

    /// <summary>IPX object id to write to (io "cmd" object, or ana setpoint).</summary>
    public long? CommandId { get; set; }

    /// <summary>Light only: ana object holding the current level (0-100).</summary>
    public long? BrightnessStateId { get; set; }

    /// <summary>Light only: ana object receiving the requested level (0-100).</summary>
    public long? BrightnessCommandId { get; set; }

    public string? DeviceClass { get; set; }
    public string? Unit { get; set; }
    public string? StateClass { get; set; }
    public string? Icon { get; set; }

    /// <summary>binary_sensor only: report ON when the IPX input is off.</summary>
    public bool Invert { get; set; }

    /// <summary>sensor only: number of decimals published.</summary>
    public int? Decimals { get; set; }

    /// <summary>number only: lowest and highest value accepted (defaults 0 and 100).</summary>
    public double? Min { get; set; }

    public double? Max { get; set; }

    internal long? IdForIndex => StateId ?? CommandId;
}
