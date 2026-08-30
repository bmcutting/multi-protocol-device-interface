using DeviceChannel.Abstractions;

namespace DeviceChannel.Configuration;

/// <summary>
/// Una instalación ya construida: los canales abiertos por el archivo y los
/// datos que se pueden pedir a través de ellos.
/// </summary>
public sealed class Installation : IAsyncDisposable
{
    private readonly Dictionary<string, IDeviceChannel> _channels;

    internal Installation(Dictionary<string, IDeviceChannel> channels, IReadOnlyList<ConfiguredData> data)
    {
        _channels = channels;
        Data = data;
    }

    public IReadOnlyList<ConfiguredData> Data { get; }

    public IReadOnlyCollection<IDeviceChannel> Channels => _channels.Values;

    /// <summary>Nombre legible del canal, para los mensajes de la aplicación.</summary>
    public string NameOf(IDeviceChannel channel) =>
        _channels.FirstOrDefault(pair => pair.Value == channel).Key ?? "canal";

    public async ValueTask DisposeAsync()
    {
        foreach (IDeviceChannel channel in _channels.Values)
            await channel.DisposeAsync();
    }
}

/// <summary>
/// Un dato de la instalación con lo que la aplicación necesita para pedirlo y
/// para presentarlo. El protocolo queda dentro de <see cref="Data"/>.
/// </summary>
public sealed record ConfiguredData
{
    public required string Name { get; init; }

    public required IDeviceChannel Channel { get; init; }

    public required DeviceData Data { get; init; }

    /// <summary>Nombre del protocolo, solo para mostrarlo.</summary>
    public required string Protocol { get; init; }

    public string? Device { get; init; }

    public string? Unit { get; init; }

    public bool IsWritable => Data.Access is DataAccess.ReadWrite;
}
