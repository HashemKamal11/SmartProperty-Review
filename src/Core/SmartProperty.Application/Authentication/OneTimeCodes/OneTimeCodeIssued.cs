namespace SmartProperty.Application.Authentication.OneTimeCodes;

/// <summary>
/// Outcome of sending a code. Never carries the code, the phone number, or the email.
/// </summary>
public sealed record OneTimeCodeIssued(DateTimeOffset ExpiresAt);
