#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record VerifyPhoneResponse(
    Guid UserId,
    bool PhoneVerified,
    DateTimeOffset VerifiedAt,
    string Status,
    string Message);
