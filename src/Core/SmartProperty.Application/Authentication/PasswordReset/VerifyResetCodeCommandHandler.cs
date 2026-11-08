using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PasswordReset;

/// <remarks>
/// The reset code keeps its usual protection: six digits, five minutes, five wrong guesses. Entering it correctly
/// spends it and, in the same save, issues the reset token, so the code can never be replayed and the password step
/// needs no code. An unknown email, a disabled account, a wrong code and an expired code all get the same answer.
/// </remarks>
public sealed class VerifyResetCodeCommandHandler(
    IUserRepository userRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<VerifyResetCodeCommand, VerifyResetCodeResult>
{
    private const OneTimeCodePurpose Purpose = OneTimeCodePurpose.PasswordReset;

    public async Task<Result<VerifyResetCodeResult>> Handle(
        VerifyResetCodeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CodeRecipient.ValidateEmail(command.Email) is { } emailError)
        {
            return Result<VerifyResetCodeResult>.Failure(emailError);
        }

        var user = await userRepository.GetByEmailAsync(command.Email, cancellationToken);

        if (user is null || !CodeRecipient.CanReceiveCodes(user))
        {
            return Result<VerifyResetCodeResult>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
        }

        var now = dateTimeProvider.UtcNow;
        var check = await OneTimeCodeFlow.CheckAsync(
            oneTimeCodeRepository,
            unitOfWork,
            user.Id,
            Purpose,
            command.Code,
            now,
            cancellationToken);

        if (check != CodeCheck.Confirmed)
        {
            return Result<VerifyResetCodeResult>.Failure(OneTimeCodeErrors.ForCheck(Purpose, check));
        }

        // A user who verifies again gets a new token and the earlier one stops working.
        foreach (var previous in await oneTimeCodeRepository.GetUnconsumedAsync(
            user.Id,
            OneTimeCodePurpose.PasswordResetGrant,
            cancellationToken))
        {
            previous.Consume(now);
        }

        var token = OneTimeCodePolicy.GenerateGrantToken();
        var grant = OneTimeCodePolicy.IssueGrant(user.Id, token, now);

        await oneTimeCodeRepository.AddAsync(grant, cancellationToken);

        if (!await OneTimeCodeFlow.TrySaveAsync(unitOfWork, cancellationToken))
        {
            // A concurrent request spent the code first, so this one is refused like any other wrong code.
            return Result<VerifyResetCodeResult>.Failure(OneTimeCodeErrors.InvalidOrExpired(Purpose));
        }

        return Result<VerifyResetCodeResult>.Success(new VerifyResetCodeResult(token, grant.ExpiresAt));
    }
}
