#nullable enable
using System;

namespace SmartProperty.Common.Results;

public sealed class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);

    public static Result Failure(Error error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));
        return new Result(false, error);
    }
}
