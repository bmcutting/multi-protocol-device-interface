namespace DeviceChannel.Abstractions;

/// <summary>
/// Valor obtenido de un <see cref="DeviceData"/>, junto al instante en que el
/// adaptador lo obtuvo. El canal no clasifica la vigencia del valor: entrega
/// <see cref="Timestamp"/> y corresponde al consumidor aplicar su propio
/// criterio de antigüedad.
/// </summary>
public sealed record Reading
{
    public object? Value { get; }

    public DateTimeOffset Timestamp { get; }

    public bool HasValue => Value is not null;

    private Reading(object? value, DateTimeOffset timestamp)
    {
        Value = value;
        Timestamp = timestamp;
    }

    public static Reading Of(object value, DateTimeOffset timestamp) =>
        new(value, timestamp);

    public static Reading Empty(DateTimeOffset timestamp) =>
        new(null, timestamp);

    public override string ToString() => HasValue
        ? $"{Format(Value)} {Timestamp:HH:mm:ss.fff}"
        : $"[sin dato] {Timestamp:HH:mm:ss.fff}";

    private static string Format(object? value) => value switch
    {
        null => "null",
        bool[] bits => string.Join(",", bits),
        ushort[] regs => string.Join(",", regs),
        byte[] payload => System.Text.Encoding.UTF8.GetString(payload),
        _ => value.ToString() ?? "null",
    };
}
