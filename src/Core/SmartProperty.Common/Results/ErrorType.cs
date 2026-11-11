namespace SmartProperty.Common.Results;

public enum ErrorType
{
    Failure,
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unauthorized,
    TooManyRequests,

    /// <summary>A required trusted dependency cannot answer right now, so the request cannot be decided either way.</summary>
    ServiceUnavailable
}
