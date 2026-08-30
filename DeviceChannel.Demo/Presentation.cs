using DeviceChannel.Abstractions;
using DeviceChannel.Configuration;

namespace DeviceChannel.Demo;

/// <summary>
/// Turns a reading into something a person can read. Everything here is
/// presentation: the contract does not know about units or wording.
/// </summary>
public static class Presentation
{
    public static string Describe(this ConfiguredData data, Reading reading)
    {
        if (!reading.HasValue)
            return "no data yet";

        return reading.Value switch
        {
            bool active => data.Unit switch
            {
                "light" => active ? "on" : "off",
                "bed" => active ? "occupied" : "free",
                _ => active ? "yes" : "no",
            },
            double number => $"{number:0.0} {data.Unit}",
            _ => $"{reading.Value}",
        };
    }

    /// <summary>Whether the device takes a switch rather than a number.</summary>
    public static bool IsSwitch(this ConfiguredData data) =>
        data.Data switch
        {
            ModbusDeviceData modbus => modbus.DataType is ModbusDataType.Bool,
            MqttDeviceData mqtt => mqtt.PayloadType is MqttPayloadType.Boolean,
            _ => false,
        };

    public static string InputHint(this ConfiguredData data) =>
        data.IsSwitch() ? "on / off" : $"number in {data.Unit}";
}
