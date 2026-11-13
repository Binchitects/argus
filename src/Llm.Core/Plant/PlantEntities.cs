namespace Llm.Core.Plant;

/// <summary>A site of the company's: a plant, a building, a yard; its areas, assets and points under it, in its time zone.</summary>
public sealed class PlantSite
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>IANA time zone (Europe/Berlin): shift reports and charts read its hours.</summary>
    public string TimeZone { get; set; } = "UTC";
    /// <summary>The groups whose people may see it (empty: everyone with the plant feature).</summary>
    public List<Guid> GroupIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A part of a site: an area, a line, a room, a floor; one under another.</summary>
public sealed class PlantArea
{
    public const string Area = "area", Line = "line", Room = "room", Floor = "floor";
    public static readonly string[] Kinds = [Area, Line, Room, Floor];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SiteId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public string Kind { get; set; } = Area;
    public int Position { get; set; }
}

/// <summary>A thing on a site: a machine, a sensor, a camera, a PLC, a door, a meter; where it is, and what it is.</summary>
public sealed class PlantAsset
{
    public const string Machine = "machine", Sensor = "sensor", Camera = "camera", Controller = "controller", Door = "door", Meter = "meter", Other = "other";
    public static readonly string[] Kinds = [Machine, Sensor, Camera, Controller, Door, Meter, Other];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SiteId { get; set; }
    public Guid? AreaId { get; set; }
    public required string Name { get; set; }
    public string Kind { get; set; } = Machine;
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>On its area's floor plan, as shares of its width and height (0 to 1); null: not placed.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A value an asset gives: a temperature, a state, a count; its unit and type, where it is read from (a protocol and
/// its address there), and its limits (an alarm's, when it passes them).
/// </summary>
public sealed class PlantPoint
{
    public const string Number = "number", Boolean = "boolean", Text = "text", State = "state";
    public static readonly string[] Types = [Number, Boolean, Text, State];
    public const string Manual = "manual", Mqtt = "mqtt", OpcUa = "opcua", Modbus = "modbus", Bacnet = "bacnet", Snmp = "snmp", Http = "http";
    public static readonly string[] Sources = [Manual, Mqtt, OpcUa, Modbus, Bacnet, Snmp, Http];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AssetId { get; set; }
    /// <summary>Its name on its asset (temperature, state, flow); its full name is the asset's and its.</summary>
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string DataType { get; set; } = Number;
    public string? Unit { get; set; }
    public string Source { get; set; } = Manual;
    /// <summary>Where the source has it: an MQTT topic, an OPC UA node id, a Modbus unit:register, a BACnet object.</summary>
    public string? Address { get; set; }
    /// <summary>Its limits: low-low, low, high, high-high (an alarm's), and the deadband a value must come back by.</summary>
    public double? LowLow { get; set; }
    public double? Low { get; set; }
    public double? High { get; set; }
    public double? HighHigh { get; set; }
    public double? Deadband { get; set; }
    /// <summary>For a state: what each value means ("0=stopped,1=running,2=fault").</summary>
    public string? States { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
