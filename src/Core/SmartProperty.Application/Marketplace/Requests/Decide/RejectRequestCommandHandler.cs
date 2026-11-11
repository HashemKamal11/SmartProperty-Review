using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.Requests.Decide;

/// <summary>
/// The advertiser rejects an open request. The advertiser is the author of the approved version the request was made
/// against; a request on anyone else's listing is the same not-found as a missing one.
/// </summary>
/// <remarks>
/// Rejecting only reduces what the advertiser has promised, so it does not need the advertising grant to still be in
/// force. Accepting does.
/// </remarks>
public sealed class RejectRequestCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestWriteRepository repository,
    IDateTimeProvider clock)
    : ICommandHandler<RejectRequestCommand, RequestDecisionResult>
{
    public async Task<Result<RequestDecisionResult>> Handle(
        RejectRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<RequestDecisionResult>.Failure(accountError);
        }

        var located = await ReceivedRequest.LocateAsync(repository, acting.UserId, command.Kind, command.RequestId, cancellationToken);

        if (located is null)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
        }

        if (await repository.LockPropertyAsync(located.PropertyId, cancellationToken) is null)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
        }

        var now = clock.UtcNow;

        try
        {
            if (command.Kind == RequestKind.Purchase)
            {
                var request = await repository.GetPurchaseRequestAsync(command.RequestId, cancellationToken);

                if (request is null)
                {
                    return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
                }

                request.Reject(now);

                return await SaveAsync(RequestKind.Purchase, request.Id, request.Status.ToString(), now, cancellationToken);
            }

            var rental = await repository.GetRentalRequestAsync(command.RequestId, cancellationToken);

            if (rental is null)
            {
                return Result<RequestDecisionResult>.Failure(RequestErrors.RequestNotFound);
            }

            rental.Reject(now);

            return await SaveAsync(RequestKind.Rental, rental.Id, rental.Status.ToString(), now, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Result<RequestDecisionResult>.Failure(RequestErrors.NotOpen);
        }
    }

    private async Task<Result<RequestDecisionResult>> SaveAsync(
        RequestKind kind, Guid id, string status, DateTimeOffset at, CancellationToken cancellationToken)
    {
        return await RequestCommit.SaveAsync(repository, cancellationToken) is { } conflict
            ? Result<RequestDecisionResult>.Failure(conflict)
            : Result<RequestDecisionResult>.Success(new RequestDecisionResult(kind, id, status, at));
    }
}

/// <summary>Finds a request the advertiser is allowed to act on, before any lock, only to learn which property to lock.</summary>
internal sealed record LocatedReceivedRequest(Guid PropertyId, Guid ListingId, Guid SubmissionId, DateTimeOffset SubmittedAt);

internal static class ReceivedRequest
{
    /// <summary>
    /// The advertiser of a request is the author of the approved version it was made against. A request from before
    /// versions existed has no advertiser and can be acted on by no one.
    /// </summary>
    public static async Task<LocatedReceivedRequest?> LocateAsync(
        IMarketplaceRequestWriteRepository repository,
        Guid advertiserUserId,
        RequestKind kind,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return null;
        }

        Guid? submissionId;
        Guid propertyId;
        Guid listingId;
        DateTimeOffset submittedAt;

        if (kind == RequestKind.Purchase)
        {
            var request = await repository.FindPurchaseRequestAsync(requestId, cancellationToken);
            (submissionId, propertyId, listingId, submittedAt) = request is null
                ? (null, Guid.Empty, Guid.Empty, default)
                : (request.SubmissionId, request.PropertyId, request.ListingId, request.SubmittedAt);
        }
        else if (kind == RequestKind.Rental)
        {
            var request = await repository.FindRentalRequestAsync(requestId, cancellationToken);
            (submissionId, propertyId, listingId, submittedAt) = request is null
                ? (null, Guid.Empty, Guid.Empty, default)
                : (request.SubmissionId, request.PropertyId, request.ListingId, request.SubmittedAt);
        }
        else
        {
            return null;
        }

        if (submissionId is not { } versionId)
        {
            return null;
        }

        var version = await repository.GetSubmissionAsync(versionId, cancellationToken);

        return version is not null && version.Authority.ActingUserId == advertiserUserId
            ? new LocatedReceivedRequest(propertyId, listingId, versionId, submittedAt)
            : null;
    }
}
