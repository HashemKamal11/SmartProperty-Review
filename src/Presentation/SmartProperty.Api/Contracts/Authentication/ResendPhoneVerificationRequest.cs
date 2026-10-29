#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record ResendPhoneVerificationRequest(Guid? UserId);
