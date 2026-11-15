using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// The self-registration of a Pending account, shared by individual registration and company (workspace)
/// registration so both create the account, its credential and its verification codes in exactly the same way.
/// </summary>
/// <remarks>
/// <see cref="StageAsync"/> only adds to the unit of work; the caller adds whatever else belongs to the same
/// registration and saves once. <see cref="SendCodesAsync"/> is called only after that save succeeded, so a rejected
/// registration never texts or emails anyone.
/// </remarks>
internal sealed class PendingAccountRegistrar(
    IUserRepository userRepository,
    IUserCredentialRepository userCredentialRepository,
    IOneTimeCodeRepository oneTimeCodeRepository,
    IPasswordHasher passwordHasher,
    ISmsSender smsSender,
    IEmailSender emailSender,
    IContactVerificationPolicy contactVerificationPolicy)
{
    /// <summary>
    /// Checks for an existing email and phone number, then stages the user, the credential and the verification
    /// codes. The command must already have passed <see cref="RegisterCommandValidator"/>.
    /// </summary>
    public async Task<Result<StagedAccount>> StageAsync(
        RegisterCommand command,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken)
    {
        // Constructed before the duplicate checks so the lookups use the email and phone number exactly as User
        // normalizes and stores them. Nothing is tracked until every check has passed.
        var user = new User(
            Guid.NewGuid(),
            command.Email,
            command.FirstName,
            command.LastName,
            registeredAt,
            command.PhoneNumber);

        // Rejects an already registered email or phone number before hashing. A concurrent registration can
        // still pass these checks; the unique indexes then reject the save (see TranslateSaveCollision).
        if (await userRepository.GetByEmailAsync(user.Email, cancellationToken) is not null)
        {
            return Result<StagedAccount>.Failure(RegisterErrors.EmailAlreadyExists);
        }

        if (await userRepository.GetByPhoneNumberAsync(user.PhoneNumber!, cancellationToken) is not null)
        {
            return Result<StagedAccount>.Failure(RegisterErrors.PhoneNumberAlreadyExists);
        }

        var credential = new UserCredential(user.Id, passwordHasher.Hash(command.Password), registeredAt);

        // With auto-verification on, the phone is trusted as entered: no code is issued and no SMS is sent.
        string? phoneCode = null;
        OneTimeCode? phoneVerification = null;

        if (contactVerificationPolicy.AutoVerifyPhone)
        {
            user.VerifyPhone(registeredAt);
        }
        else
        {
            phoneCode = OneTimeCodePolicy.GenerateCode();
            phoneVerification = OneTimeCodePolicy.Issue(
                user.Id,
                OneTimeCodePurpose.PhoneVerification,
                phoneCode,
                registeredAt);
        }

        var emailCode = OneTimeCodePolicy.GenerateCode();
        var emailVerification = OneTimeCodePolicy.Issue(
            user.Id,
            OneTimeCodePurpose.EmailVerification,
            emailCode,
            registeredAt);

        await userRepository.AddAsync(user, cancellationToken);
        await userCredentialRepository.AddAsync(credential, cancellationToken);
        if (phoneVerification is not null)
        {
            await oneTimeCodeRepository.AddAsync(phoneVerification, cancellationToken);
        }

        await oneTimeCodeRepository.AddAsync(emailVerification, cancellationToken);

        return Result<StagedAccount>.Success(
            new StagedAccount(user, phoneCode, phoneVerification?.ExpiresAt, emailCode, emailVerification.ExpiresAt));
    }

    /// <summary>
    /// The registration error for a unique-index collision on the account's email or phone number, or null when the
    /// collision is on something else.
    /// </summary>
    public static Error? TranslateSaveCollision(UniqueConstraintViolationException exception) =>
        exception.Constraint switch
        {
            PersistenceConstraint.UserEmail => RegisterErrors.EmailAlreadyExists,
            PersistenceConstraint.UserPhoneNumber => RegisterErrors.PhoneNumberAlreadyExists,
            _ => null
        };

    /// <summary>
    /// Sends the codes staged for the account. If a send fails the account still exists, and the caller can request
    /// another code.
    /// </summary>
    public async Task SendCodesAsync(StagedAccount account, CancellationToken cancellationToken)
    {
        if (account.PhoneCode is not null)
        {
            await smsSender.SendAsync(
                account.User.PhoneNumber!,
                OneTimeCodePolicy.BuildPhoneMessage(account.PhoneCode),
                cancellationToken);
        }

        var (subject, body) = OneTimeCodePolicy.BuildEmailVerificationMessage(account.EmailCode);
        await emailSender.SendAsync(account.User.Email, subject, body, cancellationToken);
    }
}

internal sealed record StagedAccount(
    User User,
    string? PhoneCode,
    DateTimeOffset? PhoneVerificationExpiresAt,
    string EmailCode,
    DateTimeOffset EmailVerificationExpiresAt)
{
    // Keeps the one-time codes out of logs and debugger displays of the record.
    public override string ToString() => nameof(StagedAccount);
}
