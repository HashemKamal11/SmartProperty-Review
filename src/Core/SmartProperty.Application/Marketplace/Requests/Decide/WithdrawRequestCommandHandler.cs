using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.Requests.Decide;

/// <summary>
/// A customer withdraws their own open request. Only the person who made it can; anyone else, and a request that
/// does not exist, get the same not-found.
/// </summary>
/// <remarks>
/// A request that was decided, withdrawn or closed cannot be withdrawn again, and an accepted one is never
/// withdrawn here: its commitment is a separate agreement that this action does not touch.
/// </remarks>
public sealed class WithdrawRequestCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestWriteRepository repository,
    IDateTimeProvider clock)
    : ICommandHandler<WithdrawRequestCommand, RequestDecisionResult>
{
    public async Task<Result<RequestDecisionResult>> Handle(
        WithdrawRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<RequestDecisionResult>.Failure(accountError);
        }

        var now = clock.UtcNow;

        if (command.Kind == RequestKind.Purchase)
        {
            var known = command.RequestId == Guid.Empty ? null : await repository.FindPurchaseRequestAsync(command.RequestId, cancellationToken);

            if (known is null || known.BuyerUserId != acting.UserId)
            {
                return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
            }

            if (await repository.LockPropertyAsync(known.PropertyId, cancellationToken) is null)
            {
                return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
            }

            var request = await repository.GetPurchaseRequestAsync(command.RequestId, cancellationToken);

            if (request is null || request.BuyerUserId != acting.UserId)
            {
                return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
            }

            try
            {
                request.Withdraw(now);
            }
            catch (InvalidOperationException)
            {
                return Result<RequestDecisionResult>.Failure(RequestErrors.NotOpen);
            }

            if (await RequestCommit.SaveAsync(repository, cancellationToken) is { } conflict)
            {
                return Result<RequestDecisionResult>.Failure(conflict);
            }

            return Result<RequestDecisionResult>.Success(
                new RequestDecisionResult(RequestKind.Purchase, request.Id, request.Status.ToString(), now));
        }

        if (command.Kind != RequestKind.Rental)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
        }

        var knownRental = command.RequestId == Guid.Empty ? null : await repository.FindRentalRequestAsync(command.RequestId, cancellationToken);

        if (knownRental is null || knownRental.RenterUserId != acting.UserId)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
        }

        if (await repository.LockPropertyAsync(knownRental.PropertyId, cancellationToken) is null)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
        }

        var rental = await repository.GetRentalRequestAsync(command.RequestId, cancellationToken);

        if (rental is null || rental.RenterUserId != acting.UserId)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
        }

        try
        {
            rental.Withdraw(now);
        }
        catch (InvalidOperationException)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.NotOpen);
        }

        if (await RequestCommit.SaveAsync(repository, cancellationToken) is { } rentalConflict)
        {
            return Result<RequestDecisionResult>.Failure(rentalConflict);
        }

        return Result<RequestDecisionResult>.Success(
            new RequestDecisionResult(RequestKind.Rental, rental.Id, rental.Status.ToString(), now));
    }
}
