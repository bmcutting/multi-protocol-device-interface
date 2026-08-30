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

    /// <param name="period">
    /// Período con el que el consumidor espera tener noticias del dato. Cada
    /// canal lo cumple con los medios de su protocolo: el que no dispone de
    /// notificación consulta con esa cadencia; el que la recibe la aprovecha y
    /// solo interviene cuando transcurre ese tiempo sin novedades.
    /// </param>
    IAsyncEnumerable<Reading> SubscribeAsync(
        DeviceData data,
        TimeSpan period,
        CancellationToken ct = default);
}
