#nullable enable
namespace SmartProperty.Api.Contracts;

public sealed record ApiErrorResponse(
    string Code,
    string Message,
    int Status,
    IReadOnlyDictionary<string, string[]>? FieldErrors,
    string CorrelationId);
