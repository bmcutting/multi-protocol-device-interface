namespace DeviceChannel.Abstractions;

/// <summary>
/// Contrato de comunicación con un dispositivo, independiente del protocolo
/// utilizado por la implementación.
/// </summary>
public interface IDeviceChannel : IAsyncDisposable
{
    Guid DeviceId { get; }

    bool IsConnected { get; }

    Task<Result> ConnectAsync(CancellationToken ct = default);

    Task<Result> DisconnectAsync(CancellationToken ct = default);

    Task<Result<Reading>> ReadAsync(DeviceData data, CancellationToken ct = default);

    Task<Result> WriteAsync(DeviceData data, object value, CancellationToken ct = default);

    IAsyncEnumerable<Reading> SubscribeAsync(
        DeviceData data,
        TimeSpan maxStaleness,
        CancellationToken ct = default);
}
