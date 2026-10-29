#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record ResendEmailVerificationRequest(Guid? UserId);
