using DeviceChannel.TestSlave;

int port = args.Length > 0 && int.TryParse(args[0], out int custom) ? custom : 502;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Room 302 Modbus slave listening on 127.0.0.1:{port} (unit {ModbusTestSlave.UnitId}).");
Console.WriteLine("  Holding 0..1  room temperature");
Console.WriteLine("  Holding 2..3  thermostat setpoint");
Console.WriteLine("  Coil 0        light");
Console.WriteLine("\nCtrl+C para terminar.\n");

await new ModbusTestSlave(port).RunAsync(cts.Token);
