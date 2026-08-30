namespace DeviceChannel.Demo;

/// <summary>
/// Demo configuration. The devices themselves come from the installation file;
/// these options only choose which file to read and whether to simulate the
/// field devices it describes.
/// </summary>
public sealed record Options
{
    public string InstallationFile { get; init; } = "installation.json";

    /// <summary>Run the simulated slave and sensors alongside the demo.</summary>
    public bool Simulate { get; init; } = true;

    public int SimulatedModbusPort { get; init; } = 5020;

    public string SimulatedMqttHost { get; init; } = "127.0.0.1";

    public int SimulatedMqttPort { get; init; } = 1883;

    public bool Help { get; init; }

    public static Options Read(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i].ToLowerInvariant();
            string? value = i + 1 < args.Length ? args[i + 1] : null;

            switch (argument)
            {
                case "--help" or "-h" or "-?":
                    return options with { Help = true };

                case "--file" or "-f" when value is not null:
                    options = options with { InstallationFile = value };
                    i++;
                    break;

                case "--no-simulator":
                    options = options with { Simulate = false };
                    break;

                case "--simulated-modbus-port" when int.TryParse(value, out int port):
                    options = options with { SimulatedModbusPort = port };
                    i++;
                    break;
            }
        }

        return options;
    }

    public static void ShowHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  IDeviceChannel demo over Modbus TCP and MQTT.");
        Console.WriteLine();
        Console.WriteLine("  The devices are described by an installation file, not by this");
        Console.WriteLine("  program. Adding one is editing that file, not recompiling.");
        Console.WriteLine();
        Console.WriteLine("  Options");
        Console.WriteLine();
        Console.WriteLine("    --file <path>            Installation file to read.");
        Console.WriteLine("                             Defaults to installation.json.");
        Console.WriteLine("    --no-simulator           Do not run the simulated field devices,");
        Console.WriteLine("                             so the file points at real ones.");
        Console.WriteLine("    --simulated-modbus-port  Port for the simulated slave.");
        Console.WriteLine("                             Defaults to 5020.");
        Console.WriteLine();
        Console.WriteLine("  Examples");
        Console.WriteLine();
        Console.WriteLine("    dotnet run --project DeviceChannel.Demo");
        Console.WriteLine("    dotnet run --project DeviceChannel.Demo -- --file plant.json --no-simulator");
        Console.WriteLine();
    }
}
