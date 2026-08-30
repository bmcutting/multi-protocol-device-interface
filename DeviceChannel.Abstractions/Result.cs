namespace DeviceChannel.Abstractions;

/// <summary>
/// Resultado de una operación que puede fallar por causas previstas, como la
/// pérdida del enlace o la ausencia de respuesta del dispositivo, sin recurrir
/// a excepciones.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public string? Error { get; }

    protected Result(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);

    public static Result Failure(string error) => new(false, error);

    public override string ToString() => IsSuccess ? "Ok" : $"Error: {Error}";
}

/// <summary>Resultado que incluye un valor cuando la operación es correcta.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, string? error)
        : base(isSuccess, error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"No hay valor en un resultado fallido: {Error}");

    public static Result<T> Success(T value) => new(true, value, null);

    public static new Result<T> Failure(string error) => new(false, default, error);
}
