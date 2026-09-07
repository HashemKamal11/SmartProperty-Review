#nullable enable
using System;

namespace SmartProperty.Common.Results;

public sealed class Result<T>
{
    private readonly T? _value;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public T Value
    {
        get
        {
            if (!IsSuccess) throw new InvalidOperationException("Cannot access the Value of a failed Result.");
            return _value!;
        }
    }

    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(true, value, null);
    }

    public static Result<T> Failure(Error error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));
        return new Result<T>(false, default, error);
    }
}
