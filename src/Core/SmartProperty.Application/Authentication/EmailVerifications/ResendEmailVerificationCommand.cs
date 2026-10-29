using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authentication.OneTimeCodes;

namespace SmartProperty.Application.Authentication.EmailVerifications;

public sealed record ResendEmailVerificationCommand(Guid UserId) : ICommand<OneTimeCodeIssued>;
