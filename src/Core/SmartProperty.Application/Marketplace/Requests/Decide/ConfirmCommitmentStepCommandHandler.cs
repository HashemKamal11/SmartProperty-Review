using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.Requests.Decide;

/// <summary>
/// The owner records that the agreement was signed, that the property was handed over, or that a tenant returned it.
/// </summary>
/// <remarks>
/// <para>
/// These are the owner's statements, nothing more: no signature, document or payment is checked, and no lease is
/// produced. Only the commitment's owner can take a step; a commitment that is not theirs is the same not-found as a
/// missing one.
/// </para>
/// <para>
/// <b>The order is fixed:</b> signed, then handed over, then (for a rental) returned. Signing is accepted only while the
/// signing deadline has not passed; an unsigned commitment past its deadline is expired by the next acceptance on the
/// property. A rental stays occupied, blocking its dates, until the owner confirms the return: nothing infers the end
/// of an occupancy from the calendar, and nothing handles an overstay automatically.
/// </para>
/// </remarks>
public sealed class ConfirmCommitmentStepCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestWriteRepository repository,
    IDateTimeProvider clock)
    : ICommandHandler<ConfirmCommitmentStepCommand, CommitmentStepResult>
{
    public async Task<Result<CommitmentStepResult>> Handle(
        ConfirmCommitmentStepCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<CommitmentStepResult>.Failure(accountError);
        }

        if (command.CommitmentId == Guid.Empty || !Enum.IsDefined(command.Step))
        {
            return Result<CommitmentStepResult>.Failure(RequestErrors.CommitmentNotFound);
        }

        var known = await repository.FindCommitmentAsync(command.CommitmentId, cancellationToken);

        if (known is null || known.OwnerUserId != acting.UserId)
        {
            return Result<CommitmentStepResult>.Failure(RequestErrors.CommitmentNotFound);
        }

        if (await repository.LockPropertyAsync(known.PropertyId, cancellationToken) is null)
        {
            return Result<CommitmentStepResult>.Failure(RequestErrors.CommitmentNotFound);
        }

        var commitment = await repository.GetCommitmentAsync(command.CommitmentId, cancellationToken);

        if (commitment is null || commitment.OwnerUserId != acting.UserId)
        {
            return Result<CommitmentStepResult>.Failure(RequestErrors.CommitmentNotFound);
        }

        var now = clock.UtcNow;

        try
        {
            switch (command.Step)
            {
                case CommitmentStep.Signed:
                    commitment.ConfirmSigned(now);
                    break;
                case CommitmentStep.Handover:
                    commitment.ConfirmHandover(now);
                    break;
                default:
                    commitment.ConfirmReturn(now);
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Result<CommitmentStepResult>.Failure(RequestErrors.CommitmentStepNotAllowed);
        }

        return await RequestCommit.SaveAsync(repository, cancellationToken) is { } conflict
            ? Result<CommitmentStepResult>.Failure(conflict)
            : Result<CommitmentStepResult>.Success(new CommitmentStepResult(commitment.Id, commitment.Status.ToString(), now));
    }
}
