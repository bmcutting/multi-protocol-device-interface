namespace DeviceChannel.Abstractions;

/// <summary>
/// Identifica un dato de un dispositivo. Determina qué dato se solicita, no su
/// valor. El consumidor opera sobre <see cref="DeviceData"/>; cada canal
/// define la subclase con los campos que su protocolo requiere.
/// </summary>
public abstract record DeviceData
{
    public required string Name { get; init; }

    public DataAccess Access { get; init; } = DataAccess.ReadWrite;
}

/// <summary>
/// Acceso permitido sobre un dato. Recoge la restricción que el protocolo no
/// transporta: un registro que la instalación no permite modificar es de solo
/// lectura aunque la función de escritura exista.
/// </summary>
public enum DataAccess
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// Dato de un dispositivo Modbus TCP. <see cref="DataType"/> y
/// <see cref="WordOrder"/> determinan cómo se interpretan los registros leídos,
/// información que el protocolo no transporta.
/// </summary>
public sealed record ModbusDeviceData : DeviceData
{
    public required Uri Endpoint { get; init; }

    public required byte UnitId { get; init; }

    public required ModbusRegisterType RegisterType { get; init; }

    public required ushort StartAddress { get; init; }

    public required ModbusDataType DataType { get; init; }

    public ModbusWordOrder WordOrder { get; init; } = ModbusWordOrder.HighWordFirst;

    public ushort Length => DataType switch
    {
        ModbusDataType.Bool => 1,
        ModbusDataType.Int16 or ModbusDataType.UInt16 => 1,
        _ => 2,
    };

    public override string ToString() =>
        $"{Name} ({RegisterType} {StartAddress}+{Length} {DataType} @ {Endpoint} unit {UnitId})";
}

/// <summary>
/// Dato de una fuente MQTT. <see cref="PayloadType"/> determina cómo se
/// interpreta el contenido publicado en el tema.
/// </summary>
public sealed record MqttDeviceData : DeviceData
{
    public required string Topic { get; init; }

    public required MqttPayloadType PayloadType { get; init; }

    public override string ToString() => $"{Name} ({Topic} {PayloadType})";
}

public enum ModbusRegisterType
{
    HoldingRegister,
    InputRegister,
    Coil,
    DiscreteInput,
}

public enum ModbusDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float32,
}

/// <summary>
/// Orden de los registros de 16 bits dentro de un valor de 32. Varía según el
/// fabricante y el protocolo no lo declara.
/// </summary>
public enum ModbusWordOrder
{
    HighWordFirst,
    LowWordFirst,
}

public enum MqttPayloadType
{
    Number,
    Boolean,
    Text,
}
