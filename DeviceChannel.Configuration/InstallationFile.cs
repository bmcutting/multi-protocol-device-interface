using System.Text.Json.Serialization;

namespace DeviceChannel.Configuration;

/// <summary>
/// Contenido del archivo de instalación tal como se lee del disco. Es la forma
/// del JSON, no el modelo con el que trabaja la aplicación.
/// </summary>
public sealed class InstallationFile
{
    public List<SourceEntry> Sources { get; init; } = [];

    public List<DataEntry> Data { get; init; } = [];
}

/// <summary>
/// Un origen de datos: un esclavo Modbus o un broker MQTT. Los datos lo
/// referencian por nombre para no repetir la dirección en cada entrada.
/// </summary>
public sealed class SourceEntry
{
    public required string Name { get; init; }

    public required string Protocol { get; init; }

    /// <summary>Modbus: <c>tcp://host:puerto</c>. MQTT: <c>host:puerto</c>.</summary>
    public required string Endpoint { get; init; }

    /// <summary>MQTT: temas a los que suscribirse al conectar.</summary>
    public List<string> TopicFilters { get; init; } = [];

    public string? User { get; init; }

    public string? Password { get; init; }
}

/// <summary>
/// Un dato concreto de la instalación. Los campos que aplican dependen del
/// protocolo del origen al que pertenece.
/// </summary>
public sealed class DataEntry
{
    public required string Name { get; init; }

    /// <summary>Nombre del <see cref="SourceEntry"/> del que se lee.</summary>
    public required string Source { get; init; }

    /// <summary>Dispositivo al que pertenece, para agrupar en la vista.</summary>
    public string? Device { get; init; }

    /// <summary>Unidad de medida, para la presentación.</summary>
    public string? Unit { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Abstractions.DataAccess Access { get; init; } = Abstractions.DataAccess.ReadWrite;

    // --- Modbus ---

    public byte UnitId { get; init; } = 1;

    public string? RegisterType { get; init; }

    public ushort StartAddress { get; init; }

    public string? DataType { get; init; }

    public string? WordOrder { get; init; }

    // --- MQTT ---

    public string? Topic { get; init; }

    public string? PayloadType { get; init; }
}
