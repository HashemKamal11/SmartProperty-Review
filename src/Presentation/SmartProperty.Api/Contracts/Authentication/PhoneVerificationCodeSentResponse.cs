#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record PhoneVerificationCodeSentResponse(DateTimeOffset ExpiresAt);
