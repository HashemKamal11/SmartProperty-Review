using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authentication.OneTimeCodes;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

public sealed record ResendPhoneVerificationCommand(Guid UserId) : ICommand<OneTimeCodeIssued>;
