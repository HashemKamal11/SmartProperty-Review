using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

public sealed record ResendPhoneVerificationCommand(Guid UserId) : ICommand<PhoneVerificationIssued>;
