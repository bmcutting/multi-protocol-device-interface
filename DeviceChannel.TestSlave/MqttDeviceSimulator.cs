using System.Globalization;
using MQTTnet;
using MQTTnet.Protocol;

namespace DeviceChannel.TestSlave;

/// <summary>
/// Simulates the wireless devices of a hospital room, which publish over MQTT
/// when their state changes: the bed occupancy sensor and the humidity probe.
/// </summary>
public sealed class MqttDeviceSimulator
{
    public const string OccupancyTopic = "hospital/room302/bed/occupied";
    public const string CallTopic = "hospital/room302/call";
    public const string HumidityTopic = "hospital/room302/humidity";

    private readonly string _host;
    private readonly int _port;

    public MqttDeviceSimulator(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        IMqttClient client = new MqttClientFactory().CreateMqttClient();

        MqttClientOptions options = new MqttClientOptionsBuilder()
            .WithClientId($"room-simulator-{Guid.NewGuid():N}")
            .WithTcpServer(_host, _port)
            .Build();

        try
        {
            await client.ConnectAsync(options, ct);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            await PublishAsync(client, OccupancyTopic, "true", ct, retain: true);
            await PublishAsync(client, HumidityTopic, "45.0", ct, retain: true);

            int cycle = 0;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                cycle++;

                double humidity = 45.0 + 3.0 * Math.Sin(cycle / 4.0);
                await PublishAsync(client, HumidityTopic,
                    humidity.ToString("0.0", CultureInfo.InvariantCulture), ct, retain: true);

                // Every three cycles the patient gets up or returns to bed.
                if (cycle % 3 == 0)
                    await PublishAsync(client, OccupancyTopic,
                        (cycle % 6 == 0).ToString().ToLowerInvariant(), ct, retain: true);

                // Every seven cycles the call button is pressed.
                if (cycle % 7 == 0)
                    await PublishAsync(client, CallTopic, "true", ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync();

            client.Dispose();
        }
    }

    private static Task PublishAsync(
        IMqttClient client, string topic, string payload, CancellationToken ct, bool retain = false) =>
        client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build(),
            ct);
}
