#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record VerifyEmailResponse(
    Guid UserId,
    bool EmailVerified,
    DateTimeOffset VerifiedAt,
    string Status,
    string Message);
