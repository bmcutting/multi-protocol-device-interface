using System.Globalization;
using DeviceChannel.Abstractions;
using DeviceChannel.Configuration;
using DeviceChannel.Demo;
using DeviceChannel.TestSlave;

// ---------------------------------------------------------------------------
// Room 302 of a hospital. The devices are not written here: they come from
// installation.json, which also decides which protocol each one speaks. This
// file only knows IDeviceChannel.
// ---------------------------------------------------------------------------

var options = Options.Read(args);

if (options.Help)
{
    Options.ShowHelp();
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.OutputEncoding = System.Text.Encoding.UTF8;

Result<Installation> loaded = InstallationLoader.Load(options.InstallationFile);

if (loaded.IsFailure)
{
    Write($"Could not load the installation: {loaded.Error}", ConsoleColor.Red);
    return;
}

await using Installation installation = loaded.Value;

Console.WriteLine($"Installation loaded from {options.InstallationFile}: "
    + $"{installation.Data.Count} data points over {installation.Channels.Count} sources.");

Task modbusSlave = Task.CompletedTask;
Task mqttSensors = Task.CompletedTask;

if (options.Simulate)
{
    modbusSlave = new ModbusTestSlave(options.SimulatedModbusPort).RunAsync(cts.Token);
    mqttSensors = new MqttDeviceSimulator(options.SimulatedMqttHost, options.SimulatedMqttPort).RunAsync(cts.Token);

    Console.WriteLine("Simulating the field devices described by that file.");
    await Task.Delay(TimeSpan.FromMilliseconds(400));
}

List<ConfiguredData> room = [.. installation.Data];

ShowHeader();

bool quit = false;

while (!quit && !cts.IsCancellationRequested)
{
    ShowMenu();

    string choice = Console.ReadLine()?.Trim() ?? "0";
    Console.WriteLine();

    switch (choice)
    {
        case "1": await ConnectAsync(); break;
        case "2": await DisconnectAsync(); break;
        case "3": await ReadAllAsync(); break;
        case "4": await WriteOneAsync(); break;
        case "5": await WatchAsync(); break;
        case "0": quit = true; break;
        default: Write("  Unknown option.", ConsoleColor.DarkYellow); break;
    }

    Console.WriteLine();
}

cts.Cancel();
await Task.WhenAll(
    modbusSlave.ContinueWith(_ => { }),
    mqttSensors.ContinueWith(_ => { }));

Console.WriteLine("Bye.");
return;

// ---------------------------------------------------------------------------
// Contract operations. None of them knows which protocol is underneath.
// ---------------------------------------------------------------------------

async Task ConnectAsync()
{
    foreach (IDeviceChannel channel in installation.Channels)
    {
        Result result = await channel.ConnectAsync(cts.Token);

        if (result.IsSuccess)
            Write($"  {installation.NameOf(channel)}: connected.", ConsoleColor.Green);
        else
            Write($"  {installation.NameOf(channel)}: could NOT connect. {result.Error}", ConsoleColor.Red);
    }
}

async Task DisconnectAsync()
{
    foreach (IDeviceChannel channel in installation.Channels)
    {
        Result result = await channel.DisconnectAsync(cts.Token);

        if (result.IsSuccess)
            Write($"  {installation.NameOf(channel)}: disconnected.", ConsoleColor.DarkGray);
        else
            Write($"  {installation.NameOf(channel)}: error while disconnecting. {result.Error}", ConsoleColor.Red);
    }
}

async Task ReadAllAsync()
{
    Console.WriteLine("  Room 302 status");
    Console.WriteLine("  ---------------------------------------------------------------");

    foreach (ConfiguredData data in room)
    {
        Result<Reading> result = await data.Channel.ReadAsync(data.Data, cts.Token);

        if (result.IsFailure)
        {
            Write($"  {data.Name,-22} ERROR   {result.Error}", ConsoleColor.Red);
            continue;
        }

        Reading reading = result.Value;
        string value = data.Describe(reading);

        ConsoleColor color = reading.HasValue ? ConsoleColor.White : ConsoleColor.DarkYellow;
        string stamp = reading.HasValue
            ? $"  (read at {reading.Timestamp.ToLocalTime():HH:mm:ss})"
            : string.Empty;

        Write($"  {data.Name,-22} {value,-14} [{data.Protocol}]{stamp}", color);
    }

    Console.WriteLine();
    Console.WriteLine("  \"no data yet\" is not an error: the channel works and that");
    Console.WriteLine("  device simply has not published anything so far.");
}

async Task WriteOneAsync()
{
    List<ConfiguredData> writable = room.Where(d => d.IsWritable).ToList();

    Console.WriteLine("  Which device do you want to write to?");
    Console.WriteLine();

    for (int i = 0; i < writable.Count; i++)
        Console.WriteLine($"    {i + 1}) {writable[i].Name,-22} [{writable[i].Protocol}]   {writable[i].InputHint()}");

    foreach (ConfiguredData readOnly in room.Where(d => !d.IsWritable))
        Write($"    -  {readOnly.Name,-22} [{readOnly.Protocol}]   read-only", ConsoleColor.DarkGray);

    Console.Write("  > ");

    if (!int.TryParse(Console.ReadLine(), out int chosen) || chosen < 1 || chosen > writable.Count)
    {
        Write("  Invalid option.", ConsoleColor.DarkYellow);
        return;
    }

    ConfiguredData target = writable[chosen - 1];
    object value;

    if (target.IsSwitch())
    {
        string question = target.Unit switch
        {
            "light" => "  Turn the light on? (y/n) > ",
            "bed" => "  Mark the bed as occupied? (y/n) > ",
            _ => "  Set to true? (y/n) > ",
        };

        Console.Write(question);
        string answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "n";
        value = answer is "y" or "yes";
    }
    else
    {
        Console.Write($"  New value in {target.Unit} > ");

        if (!TryParseNumber(Console.ReadLine(), out double number))
        {
            Write("  That is not a number.", ConsoleColor.DarkYellow);
            return;
        }

        value = number;
    }

    Console.WriteLine();
    Result result = await target.Channel.WriteAsync(target.Data, value, cts.Token);

    if (result.IsFailure)
    {
        Write($"  Could not write: {result.Error}", ConsoleColor.Red);
        return;
    }

    Write($"  Written to {target.Name} over {target.Protocol}.", ConsoleColor.Green);

    // On MQTT the value travels to the broker and comes back as a publication,
    // so the read-back is not immediate the way a Modbus register is.
    if (target.Protocol == "MQTT")
        await Task.Delay(TimeSpan.FromMilliseconds(300));

    Result<Reading> readBack = await target.Channel.ReadAsync(target.Data, cts.Token);

    if (readBack.IsSuccess)
        Console.WriteLine($"  Check: it now reads {target.Describe(readBack.Value)}.");
}

async Task WatchAsync()
{
    Console.WriteLine("  Subscribing to every device for 20 seconds.");
    Console.WriteLine();
    Console.WriteLine("  Modbus has no notifications: the adapter polls every 2 s and only");
    Console.WriteLine("  emits when the value changed. MQTT does notify, and after 2 s");
    Console.WriteLine("  without news the channel repeats the last known value so that");
    Console.WriteLine("  silence is not mistaken for a steady process.");
    Console.WriteLine("  ---------------------------------------------------------------");
    Console.WriteLine();

    using var watch = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    watch.CancelAfter(TimeSpan.FromSeconds(20));

    IEnumerable<Task> tasks = room
        .Where(d => d.Channel.IsConnected)
        .Select(d => WatchOneAsync(d, watch.Token));

    try
    {
        await Task.WhenAll(tasks);
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine();
    Console.WriteLine("  Watch finished.");
}

async Task WatchOneAsync(ConfiguredData data, CancellationToken ct)
{
    DateTimeOffset? previous = null;

    try
    {
        await foreach (Reading reading in data.Channel.SubscribeAsync(
            data.Data, TimeSpan.FromSeconds(2), ct))
        {
            // The channel repeats the last known value when the deadline
            // expires with no news. The Timestamp, unchanged, gives it away.
            bool repeated = previous == reading.Timestamp;
            previous = reading.Timestamp;

            string time = reading.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            string value = data.Describe(reading);
            string note = repeated ? "  (unchanged)" : string.Empty;

            ConsoleColor color = !reading.HasValue
                ? ConsoleColor.DarkYellow
                : repeated ? ConsoleColor.DarkGray : ConsoleColor.White;

            Write($"  [{time}] {data.Name,-22} {value,-14} [{data.Protocol}]{note}", color);
        }
    }
    catch (OperationCanceledException)
    {
    }
}

// ---------------------------------------------------------------------------
// Presentation
// ---------------------------------------------------------------------------

void ShowHeader()
{
    Console.WriteLine();
    Console.WriteLine("  ROOM 302 - One contract, two protocols");
    Console.WriteLine("  ===============================================================");
    Console.WriteLine();

    foreach (IGrouping<string, ConfiguredData> group in room.GroupBy(d => d.Protocol))
    {
        Console.WriteLine($"  Over {group.Key}");

        foreach (ConfiguredData data in group)
        {
            string access = data.IsWritable ? "read / write" : "read-only";
            Console.WriteLine($"    - {data.Name,-22} {access}");
        }

        Console.WriteLine();
    }

    Console.WriteLine("  This menu cannot tell them apart: it talks to all of them");
    Console.WriteLine("  through the same interface, IDeviceChannel. Which protocol");
    Console.WriteLine("  each one speaks is decided by installation.json.");
    Console.WriteLine();
}

void ShowMenu()
{
    Console.WriteLine("  ---------------------------------------------------------------");
    Console.Write("  Channels:  ");

    foreach (IDeviceChannel channel in installation.Channels)
    {
        bool connected = channel.IsConnected;
        Write($"{installation.NameOf(channel)} {(connected ? "connected" : "disconnected")}   ",
            connected ? ConsoleColor.Green : ConsoleColor.DarkGray, newLine: false);
    }

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("    1) Connect          (ConnectAsync)");
    Console.WriteLine("    2) Disconnect       (DisconnectAsync)");
    Console.WriteLine("    3) Read the room    (ReadAsync)");
    Console.WriteLine("    4) Write a value    (WriteAsync)");
    Console.WriteLine("    5) Watch for 20 s   (SubscribeAsync)");
    Console.WriteLine("    0) Quit");
    Console.Write("  > ");
}

// Accepts both 21.5 and 21,5 whatever the console culture, and never treats
// the separator as a thousands mark.
static bool TryParseNumber(string? text, out double number)
{
    number = 0;

    if (string.IsNullOrWhiteSpace(text))
        return false;

    const NumberStyles Styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    return double.TryParse(text.Replace(',', '.'), Styles, CultureInfo.InvariantCulture, out number);
}

static void Write(string text, ConsoleColor color, bool newLine = true)
{
    ConsoleColor previous = Console.ForegroundColor;
    Console.ForegroundColor = color;

    if (newLine)
        Console.WriteLine(text);
    else
        Console.Write(text);

    Console.ForegroundColor = previous;
}
