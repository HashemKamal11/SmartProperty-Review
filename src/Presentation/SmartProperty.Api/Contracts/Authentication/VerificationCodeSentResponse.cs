#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record VerificationCodeSentResponse(DateTimeOffset ExpiresAt);
