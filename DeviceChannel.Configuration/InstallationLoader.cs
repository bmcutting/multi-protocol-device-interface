using System.Text.Json;
using DeviceChannel.Abstractions;
using DeviceChannel.Modbus;
using DeviceChannel.Mqtt;

namespace DeviceChannel.Configuration;

/// <summary>
/// Construye una instalación a partir de un archivo JSON. Es el único punto de
/// la aplicación donde se decide qué canal corresponde a cada origen:
/// añadir un protocolo es añadir una rama aquí y su implementación de
/// <see cref="IDeviceChannel"/>.
/// </summary>
public static class InstallationLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Result<Installation> Load(string path, TimeProvider? timeProvider = null)
    {
        string? found = Locate(path);

        if (found is null)
            return Result<Installation>.Failure($"No se encontró el archivo de instalación {path}.");

        path = found;

        InstallationFile? file;

        try
        {
            file = JsonSerializer.Deserialize<InstallationFile>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            return Result<Installation>.Failure($"El archivo {path} no es un JSON válido: {ex.Message}");
        }

        if (file is null)
            return Result<Installation>.Failure($"El archivo {path} está vacío.");

        return Build(file, timeProvider);
    }

    public static Result<Installation> Build(InstallationFile file, TimeProvider? timeProvider = null)
    {
        Dictionary<string, IDeviceChannel> channels = [];
        Dictionary<string, SourceEntry> sources = [];

        foreach (SourceEntry source in file.Sources)
        {
            if (channels.ContainsKey(source.Name))
                return Result<Installation>.Failure($"El origen '{source.Name}' está definido dos veces.");

            Result<IDeviceChannel> channel = CreateChannel(source, timeProvider);

            if (channel.IsFailure)
                return Result<Installation>.Failure(channel.Error!);

            channels[source.Name] = channel.Value;
            sources[source.Name] = source;
        }

        List<ConfiguredData> data = [];

        foreach (DataEntry entry in file.Data)
        {
            if (!channels.TryGetValue(entry.Source, out IDeviceChannel? channel))
                return Result<Installation>.Failure(
                    $"El dato '{entry.Name}' referencia el origen '{entry.Source}', que no está definido.");

            SourceEntry source = sources[entry.Source];
            Result<DeviceData> deviceData = CreateData(entry, source);

            if (deviceData.IsFailure)
                return Result<Installation>.Failure(deviceData.Error!);

            data.Add(new ConfiguredData
            {
                Name = entry.Name,
                Channel = channel,
                Data = deviceData.Value,
                Protocol = Normalize(source.Protocol) is "modbus" ? "Modbus" : "MQTT",
                Device = entry.Device,
                Unit = entry.Unit,
            });
        }

        return Result<Installation>.Success(new Installation(channels, data));
    }

    private static Result<IDeviceChannel> CreateChannel(SourceEntry source, TimeProvider? timeProvider)
    {
        switch (Normalize(source.Protocol))
        {
            case "modbus":
                if (!Uri.TryCreate(Absolute(source.Endpoint), UriKind.Absolute, out Uri? endpoint))
                    return Result<IDeviceChannel>.Failure(
                        $"El origen '{source.Name}' tiene un extremo inválido: '{source.Endpoint}'.");

                return Result<IDeviceChannel>.Success(
                    new ModbusDeviceChannel(Guid.NewGuid(), endpoint, timeProvider));

            case "mqtt":
                (string host, int port) = SplitHost(source.Endpoint);

                return Result<IDeviceChannel>.Success(new MqttDeviceChannel(
                    Guid.NewGuid(),
                    new MqttConnectionOptions
                    {
                        Host = host,
                        Port = port,
                        User = source.User,
                        Password = source.Password,
                        TopicFilters = source.TopicFilters.Count > 0 ? source.TopicFilters : ["#"],
                    },
                    timeProvider));

            default:
                return Result<IDeviceChannel>.Failure(
                    $"El origen '{source.Name}' usa un protocolo no soportado: '{source.Protocol}'.");
        }
    }

    private static Result<DeviceData> CreateData(DataEntry entry, SourceEntry source)
    {
        switch (Normalize(source.Protocol))
        {
            case "modbus":
                if (!Enum.TryParse(entry.RegisterType, ignoreCase: true, out ModbusRegisterType registerType))
                    return Result<DeviceData>.Failure(
                        $"El dato '{entry.Name}' no declara un registerType válido: '{entry.RegisterType}'.");

                if (!Enum.TryParse(entry.DataType, ignoreCase: true, out ModbusDataType dataType))
                    return Result<DeviceData>.Failure(
                        $"El dato '{entry.Name}' no declara un dataType válido: '{entry.DataType}'.");

                ModbusWordOrder wordOrder = ModbusWordOrder.HighWordFirst;

                if (entry.WordOrder is not null
                    && !Enum.TryParse(entry.WordOrder, ignoreCase: true, out wordOrder))
                    return Result<DeviceData>.Failure(
                        $"El dato '{entry.Name}' no declara un wordOrder válido: '{entry.WordOrder}'.");

                Uri.TryCreate(Absolute(source.Endpoint), UriKind.Absolute, out Uri? endpoint);

                return Result<DeviceData>.Success(new ModbusDeviceData
                {
                    Name = entry.Name,
                    Endpoint = endpoint!,
                    UnitId = entry.UnitId,
                    RegisterType = registerType,
                    StartAddress = entry.StartAddress,
                    DataType = dataType,
                    WordOrder = wordOrder,
                    Access = entry.Access,
                });

            case "mqtt":
                if (string.IsNullOrWhiteSpace(entry.Topic))
                    return Result<DeviceData>.Failure($"El dato '{entry.Name}' no declara un topic.");

                if (!Enum.TryParse(entry.PayloadType, ignoreCase: true, out MqttPayloadType payloadType))
                    return Result<DeviceData>.Failure(
                        $"El dato '{entry.Name}' no declara un payloadType válido: '{entry.PayloadType}'.");

                return Result<DeviceData>.Success(new MqttDeviceData
                {
                    Name = entry.Name,
                    Topic = entry.Topic,
                    PayloadType = payloadType,
                    Access = entry.Access,
                });

            default:
                return Result<DeviceData>.Failure(
                    $"El dato '{entry.Name}' pertenece a un origen con protocolo no soportado.");
        }
    }

    /// <summary>
    /// Busca el archivo en el directorio actual y, si no está, junto al
    /// ejecutable, de modo que la aplicación se pueda lanzar desde cualquier
    /// carpeta.
    /// </summary>
    private static string? Locate(string path)
    {
        if (File.Exists(path))
            return path;

        if (Path.IsPathRooted(path))
            return null;

        string besideExecutable = Path.Combine(AppContext.BaseDirectory, path);

        return File.Exists(besideExecutable) ? besideExecutable : null;
    }

    private static string Normalize(string protocol) => protocol.Trim().ToLowerInvariant();

    private static string Absolute(string endpoint) =>
        endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : $"tcp://{endpoint}";

    private static (string Host, int Port) SplitHost(string value)
    {
        string clean = value.Replace("tcp://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        string[] parts = clean.Split(':');

        return parts.Length == 2 && int.TryParse(parts[1], out int port)
            ? (parts[0], port)
            : (clean, 1883);
    }
}
