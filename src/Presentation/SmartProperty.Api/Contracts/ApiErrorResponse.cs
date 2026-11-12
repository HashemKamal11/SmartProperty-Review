#nullable enable
using SmartProperty.Common.Localization;

namespace SmartProperty.Api.Contracts;

/// <summary>
/// The error body. <c>message</c> is always English; <c>displayMessage</c>, added when serialized, is the same message
/// in the request's language (<c>?lang=ar</c> or <c>Accept-Language: ar</c>).
/// </summary>
public sealed record ApiErrorResponse(
    string Code,
    [property: LocalizedMessage] string Message,
    int Status,
    IReadOnlyDictionary<string, string[]>? FieldErrors,
    string CorrelationId);
