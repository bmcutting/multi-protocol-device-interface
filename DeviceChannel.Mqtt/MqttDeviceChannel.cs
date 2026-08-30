using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeviceChannel.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;

namespace DeviceChannel.Mqtt;

/// <summary>
/// Canal de comunicación con una fuente MQTT.
/// </summary>
/// <remarks>
/// El protocolo no admite consultas bajo demanda, por lo que el canal
/// almacena la última publicación recibida de cada tema y
/// <see cref="ReadAsync"/> la devuelve desde ese almacén. La entrada no se
/// elimina al leerla, con el fin de mantener la idempotencia de la operación.
/// </remarks>
public sealed class MqttDeviceChannel : IDeviceChannel
{
    private readonly MqttConnectionOptions _options;
    private readonly TimeProvider _time;
    private readonly IMqttClient _client;

    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, Reading> _lastReadings = [];
    private readonly List<TopicSubscriber> _subscribers = [];

    private bool _disposed;

    public MqttDeviceChannel(Guid deviceId, MqttConnectionOptions options, TimeProvider? timeProvider = null)
    {
        DeviceId = deviceId;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public Guid DeviceId { get; }

    public bool IsConnected => _client.IsConnected;

    public async Task<Result> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsConnected)
            return Result.Success();

        try
        {
            MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
                .WithClientId(_options.ClientId)
                .WithTcpServer(_options.Host, _options.Port)
                .WithTimeout(_options.ConnectTimeout);

            if (!string.IsNullOrEmpty(_options.User))
                builder = builder.WithCredentials(_options.User, _options.Password);

            await _client.ConnectAsync(builder.Build(), ct);

            foreach (string filter in _options.TopicFilters)
                await _client.SubscribeAsync(filter, MqttQualityOfServiceLevel.AtLeastOnce, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"No se pudo conectar con {_options.Host}:{_options.Port}: {ex.Message}");
        }
    }

    public async Task<Result> DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.DisconnectAsync(cancellationToken: ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Error al desconectar de {_options.Host}: {ex.Message}");
        }
    }

    public Task<Result<Reading>> ReadAsync(DeviceData data, CancellationToken ct = default)
    {
        if (data is not MqttDeviceData mqttData)
            return Task.FromResult(Result<Reading>.Failure($"El dato {data.Name} no es de una fuente MQTT."));

        if (!IsConnected)
            return Task.FromResult(Result<Reading>.Failure($"El canal a {_options.Host} no está conectado."));

        lock (_cacheLock)
        {
            if (!_lastReadings.TryGetValue(mqttData.Topic, out Reading? cached))
                return Task.FromResult(Result<Reading>.Success(Reading.Empty(_time.GetUtcNow())));

            return Task.FromResult(Decode(cached, mqttData));
        }
    }

    public async Task<Result> WriteAsync(DeviceData data, object value, CancellationToken ct = default)
    {
        if (data is not MqttDeviceData mqttData)
            return Result.Failure($"El dato {data.Name} no es de una fuente MQTT.");

        if (data.Access is DataAccess.ReadOnly)
            return Result.Failure($"El dato {data.Name} está declarado de solo lectura.");

        if (!IsConnected)
            return Result.Failure($"El canal a {_options.Host} no está conectado.");

        try
        {
            MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                .WithTopic(mqttData.Topic)
                .WithPayload(Convert.ToString(value, CultureInfo.InvariantCulture))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(_options.RetainWrites)
                .Build();

            await _client.PublishAsync(message, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Error al publicar en {mqttData.Topic}: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<Reading> SubscribeAsync(
        DeviceData data,
        TimeSpan maxStaleness,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (data is not MqttDeviceData mqttData)
            throw new ArgumentException($"El dato {data.Name} no es de una fuente MQTT.", nameof(data));

        if (maxStaleness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxStaleness), "El plazo debe ser mayor que cero.");

        Channel<Reading> channel = Channel.CreateBounded<Reading>(
            new BoundedChannelOptions(capacity: 64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        var subscriber = new TopicSubscriber(mqttData.Topic, channel.Writer);

        lock (_cacheLock)
            _subscribers.Add(subscriber);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Reading? next = await ReadNextOrTimeoutAsync(channel.Reader, maxStaleness, ct);

                if (next is not null)
                {
                    Result<Reading> decoded = Decode(next, mqttData);

                    if (decoded.IsSuccess)
                        yield return decoded.Value;

                    continue;
                }

                Reading? last;
                lock (_cacheLock)
                    _lastReadings.TryGetValue(mqttData.Topic, out last);

                if (last is null)
                {
                    yield return Reading.Empty(_time.GetUtcNow());
                    continue;
                }

                Result<Reading> lastDecoded = Decode(last, mqttData);

                if (lastDecoded.IsSuccess)
                    yield return lastDecoded.Value;
            }
        }
        finally
        {
            lock (_cacheLock)
                _subscribers.Remove(subscriber);

            channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;

        if (IsConnected)
            await DisconnectAsync();

        _client.Dispose();
    }

    #region Helpers

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        string topic = e.ApplicationMessage.Topic;
        byte[] payload = e.ApplicationMessage.Payload.ToArray();
        Reading reading = Reading.Of(payload, _time.GetUtcNow());

        lock (_cacheLock)
        {
            _lastReadings[topic] = reading;

            foreach (TopicSubscriber subscriber in _subscribers)
            {
                if (subscriber.Topic == topic)
                    subscriber.Writer.TryWrite(reading);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<Reading?> ReadNextOrTimeoutAsync(
        ChannelReader<Reading> reader,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        try
        {
            return await reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Result<Reading> Decode(Reading cached, MqttDeviceData data)
    {
        if (cached.Value is not byte[] payload)
            return Result<Reading>.Failure($"El valor almacenado de {data.Name} no es un payload MQTT.");

        string text = System.Text.Encoding.UTF8.GetString(payload).Trim();

        switch (data.PayloadType)
        {
            case MqttPayloadType.Text:
                return Result<Reading>.Success(Reading.Of(text, cached.Timestamp));

            case MqttPayloadType.Boolean:
                if (bool.TryParse(text, out bool flag))
                    return Result<Reading>.Success(Reading.Of(flag, cached.Timestamp));

                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double asNumber))
                    return Result<Reading>.Success(Reading.Of(asNumber != 0, cached.Timestamp));

                return Result<Reading>.Failure($"El contenido de {data.Topic} no es un booleano: '{text}'.");

            case MqttPayloadType.Number:
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
                    return Result<Reading>.Success(Reading.Of(number, cached.Timestamp));

                return Result<Reading>.Failure($"El contenido de {data.Topic} no es numérico: '{text}'.");

            default:
                return Result<Reading>.Failure($"Tipo de contenido no soportado: {data.PayloadType}");
        }
    }

    private sealed record TopicSubscriber(string Topic, ChannelWriter<Reading> Writer);

    #endregion Helpers
}

/// <summary>Parámetros de conexión con el broker.</summary>
public sealed record MqttConnectionOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 1883;

    public string ClientId { get; init; } = $"device-channel-{Guid.NewGuid():N}";

    public string? User { get; init; }

    public string? Password { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public IReadOnlyList<string> TopicFilters { get; init; } = ["#"];

    public bool RetainWrites { get; init; } = true;
}
