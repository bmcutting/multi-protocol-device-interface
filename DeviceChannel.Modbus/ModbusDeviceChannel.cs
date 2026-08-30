using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DeviceChannel.Abstractions;
using NModbus;

namespace DeviceChannel.Modbus;

/// <summary>
/// Adaptador de <see cref="IDeviceChannel"/> sobre Modbus TCP.
/// </summary>
/// <remarks>
/// El protocolo no dispone de mecanismo de suscripción, por lo que
/// <see cref="SubscribeAsync"/> se implementa mediante sondeo periódico. Dado
/// que el sondeo comparte conexión con las lecturas del consumidor, todas las
/// transacciones se serializan.
/// </remarks>
public sealed class ModbusDeviceChannel : IDeviceChannel
{
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly TimeProvider _time;

    private TcpClient? _client;
    private IModbusMaster? _master;
    private bool _disposed;

    public ModbusDeviceChannel(Guid deviceId, Uri endpoint, TimeProvider? timeProvider = null)
    {
        DeviceId = deviceId;
        _endpoint = endpoint;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Guid DeviceId { get; }

    public bool IsConnected => _client?.Connected ?? false;

    public async Task<Result> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsConnected)
            return Result.Success();

        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_endpoint.Host, _endpoint.Port, ct);
            _master = new ModbusFactory().CreateMaster(_client);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"No se pudo conectar con {_endpoint}: {ex.Message}");
        }
    }

    public Task<Result> DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _master?.Dispose();
            _master = null;
            _client?.Close();
            _client = null;
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Error al desconectar de {_endpoint}: {ex.Message}"));
        }
    }

    public async Task<Result<Reading>> ReadAsync(DeviceData data, CancellationToken ct = default)
    {
        if (data is not ModbusDeviceData modbusData)
            return Result<Reading>.Failure($"El dato {data.Name} no es de un dispositivo Modbus.");

        if (!IsConnected)
            return Result<Reading>.Failure($"El canal a {_endpoint} no está conectado.");

        await _transactionGate.WaitAsync(ct);
        try
        {
            object raw = await ReadRegistersAsync(modbusData);
            object value = Decode(raw, modbusData);
            return Result<Reading>.Success(Reading.Of(value, _time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            return Result<Reading>.Failure($"Error al leer {modbusData}: {ex.Message}");
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public async Task<Result> WriteAsync(DeviceData data, object value, CancellationToken ct = default)
    {
        if (data is not ModbusDeviceData modbusData)
            return Result.Failure($"El dato {data.Name} no es de un dispositivo Modbus.");

        if (data.Access is DataAccess.ReadOnly)
            return Result.Failure($"El dato {data.Name} está declarado de solo lectura.");

        if (!IsConnected)
            return Result.Failure($"El canal a {_endpoint} no está conectado.");

        await _transactionGate.WaitAsync(ct);
        try
        {
            switch (modbusData.RegisterType)
            {
                case ModbusRegisterType.Coil:
                    await _master!.WriteSingleCoilAsync(
                        modbusData.UnitId, modbusData.StartAddress, Convert.ToBoolean(value));
                    break;

                case ModbusRegisterType.HoldingRegister:
                    await _master!.WriteMultipleRegistersAsync(
                        modbusData.UnitId, modbusData.StartAddress, Encode(value, modbusData));
                    break;

                default:
                    return Result.Failure(
                        $"{modbusData.RegisterType} es de solo lectura; no se puede escribir en {modbusData.Name}.");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Error al escribir en {modbusData}: {ex.Message}");
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public async IAsyncEnumerable<Reading> SubscribeAsync(
        DeviceData data,
        TimeSpan maxStaleness,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (data is not ModbusDeviceData modbusData)
            throw new ArgumentException($"El dato {data.Name} no es de un dispositivo Modbus.", nameof(data));

        if (maxStaleness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxStaleness), "El plazo debe ser mayor que cero.");

        object? lastValue = null;
        bool first = true;

        while (!ct.IsCancellationRequested)
        {
            long startedAt = _time.GetTimestamp();

            Result<Reading> result = await ReadAsync(modbusData, ct);

            if (result.IsFailure)
            {
                await Task.Delay(maxStaleness, _time, ct);
                continue;
            }

            Reading reading = result.Value;
            TimeSpan elapsed = _time.GetElapsedTime(startedAt);

            if (first || !ValuesEqual(reading.Value, lastValue))
            {
                lastValue = reading.Value;
                first = false;

                yield return reading;
            }

            TimeSpan remaining = maxStaleness - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, _time, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await DisconnectAsync();
        _transactionGate.Dispose();
    }

    #region Helpers

    private async Task<object> ReadRegistersAsync(ModbusDeviceData data) => data.RegisterType switch
    {
        ModbusRegisterType.Coil =>
            await _master!.ReadCoilsAsync(data.UnitId, data.StartAddress, data.Length),
        ModbusRegisterType.DiscreteInput =>
            await _master!.ReadInputsAsync(data.UnitId, data.StartAddress, data.Length),
        ModbusRegisterType.HoldingRegister =>
            await _master!.ReadHoldingRegistersAsync(data.UnitId, data.StartAddress, data.Length),
        ModbusRegisterType.InputRegister =>
            await _master!.ReadInputRegistersAsync(data.UnitId, data.StartAddress, data.Length),
        _ => throw new NotSupportedException($"Tipo de registro no soportado: {data.RegisterType}"),
    };

    private static object Decode(object raw, ModbusDeviceData data) => raw switch
    {
        bool[] bits => bits[0],
        ushort[] registers => DecodeRegisters(registers, data),
        _ => throw new NotSupportedException($"Lectura no reconocida para {data.Name}."),
    };

    private static object DecodeRegisters(ushort[] registers, ModbusDeviceData data)
    {
        if (registers.Length < data.Length)
            throw new InvalidOperationException(
                $"El esclavo devolvió {registers.Length} registros y {data.Name} requiere {data.Length}.");

        if (data.DataType is ModbusDataType.Int16)
            return (double)(short)registers[0];

        if (data.DataType is ModbusDataType.UInt16)
            return (double)registers[0];

        if (data.DataType is ModbusDataType.Bool)
            return registers[0] != 0;

        uint combined = data.WordOrder is ModbusWordOrder.HighWordFirst
            ? ((uint)registers[0] << 16) | registers[1]
            : ((uint)registers[1] << 16) | registers[0];

        return data.DataType switch
        {
            ModbusDataType.UInt32 => (double)combined,
            ModbusDataType.Int32 => (double)(int)combined,
            ModbusDataType.Float32 => (double)BitConverter.Int32BitsToSingle((int)combined),
            _ => throw new NotSupportedException($"Tipo de dato no soportado: {data.DataType}"),
        };
    }

    private static bool ValuesEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        _ => a.Equals(b),
    };

    private static ushort[] Encode(object value, ModbusDeviceData data)
    {
        if (data.DataType is ModbusDataType.Bool)
            return [Convert.ToBoolean(value) ? (ushort)1 : (ushort)0];

        double number = Convert.ToDouble(value);

        if (data.DataType is ModbusDataType.Int16)
            return [(ushort)(short)number];

        if (data.DataType is ModbusDataType.UInt16)
            return [(ushort)number];

        uint combined = data.DataType switch
        {
            ModbusDataType.UInt32 => (uint)number,
            ModbusDataType.Int32 => (uint)(int)number,
            ModbusDataType.Float32 => (uint)BitConverter.SingleToInt32Bits((float)number),
            _ => throw new NotSupportedException($"Tipo de dato no soportado: {data.DataType}"),
        };

        ushort high = (ushort)(combined >> 16);
        ushort low = (ushort)(combined & 0xFFFF);

        return data.WordOrder is ModbusWordOrder.HighWordFirst ? [high, low] : [low, high];
    }

    #endregion Helpers
}
