using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

namespace DeviceChannel.TestSlave;

/// <summary>
/// In-memory Modbus TCP slave simulating the wired installation of a hospital
/// room: the HVAC temperature probe, the thermostat setpoint and the light.
/// </summary>
public sealed class ModbusTestSlave
{
    public const byte UnitId = 1;

    public const ushort TemperatureAddress = 0;
    public const ushort SetpointAddress = 2;
    public const ushort LightAddress = 0;

    private readonly int _port;

    public ModbusTestSlave(int port) => _port = port;

    public async Task RunAsync(CancellationToken ct)
    {
        var store = new SlaveDataStore();
        var factory = new ModbusFactory();

        store.HoldingRegisters.WritePoints(TemperatureAddress, ToRegisters(22.4f));
        store.HoldingRegisters.WritePoints(SetpointAddress, ToRegisters(21f));
        store.CoilDiscretes.WritePoints(LightAddress, [false]);

        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();

        IModbusSlaveNetwork network = factory.CreateSlaveNetwork(listener);
        network.AddSlave(factory.CreateSlave(UnitId, store));

        Task simulation = SimulateAsync(store, ct);

        try
        {
            await network.ListenAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
            await simulation;
        }
    }

    /// <summary>
    /// The room temperature drifts towards the thermostat setpoint, so that
    /// writing the setpoint has a visible effect.
    /// </summary>
    private static async Task SimulateAsync(SlaveDataStore store, CancellationToken ct)
    {
        float temperature = 22.4f;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                float setpoint = ToFloat(store.HoldingRegisters.ReadPoints(SetpointAddress, 2));
                float difference = setpoint - temperature;

                // A quarter of the remaining gap, with a floor, so that a
                // setpoint change becomes visible within a few seconds.
                float step = Math.Abs(difference) < 0.05f
                    ? difference
                    : Math.Clamp(difference * 0.25f, -5f, 5f);

                if (Math.Abs(step) < 0.1f && Math.Abs(difference) >= 0.05f)
                    step = Math.Sign(difference) * 0.1f;

                temperature += step;

                store.HoldingRegisters.WritePoints(TemperatureAddress, ToRegisters(temperature));

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ushort[] ToRegisters(float value)
    {
        uint bits = (uint)BitConverter.SingleToInt32Bits(value);
        return [(ushort)(bits >> 16), (ushort)(bits & 0xFFFF)];
    }

    private static float ToFloat(ushort[] registers)
    {
        uint bits = ((uint)registers[0] << 16) | registers[1];
        return BitConverter.Int32BitsToSingle((int)bits);
    }
}
