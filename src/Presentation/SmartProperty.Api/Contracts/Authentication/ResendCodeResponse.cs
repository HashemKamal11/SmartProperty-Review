#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// The same answer whether or not an account matches the email, so the endpoint cannot be used to find registered
/// emails.
/// </summary>
public sealed record ResendCodeResponse(string Message);
