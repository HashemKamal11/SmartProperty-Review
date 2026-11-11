using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Requests.Create;

/// <summary>
/// Makes an offer to buy against the approved version of a Sale listing.
/// </summary>
/// <remarks>
/// The buyer is the authenticated, Active user. The offer defaults to the listing's price and is always in the
/// listing's currency. A property that is committed to a sale, or has a daily or weekly rental in force, does not
/// take purchase requests: it cannot be sold until that ends. A monthly or yearly rental does not stop requests.
/// </remarks>
public sealed class CreatePurchaseRequestCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestWriteRepository repository,
    IDateTimeProvider clock)
    : ICommandHandler<CreatePurchaseRequestCommand, RequestCreatedResult>
{
    public async Task<Result<RequestCreatedResult>> Handle(
        CreatePurchaseRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<RequestCreatedResult>.Failure(accountError);
        }

        if (command.ListingId == Guid.Empty
            || command.OfferedAmount is <= 0m
            || command.Message?.Trim().Length > PurchaseRequest.MaxMessageLength)
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.Invalid(
                "A listing is required, an offer must be above zero, and the message is at most 1000 characters."));
        }

        var now = clock.UtcNow;
        var gate = await RequestableListingGate.OpenAsync(
            repository, acting.UserId, command.ListingId, ListingPurpose.Sale, now, cancellationToken);

        if (gate.IsFailure)
        {
            return Result<RequestCreatedResult>.Failure(gate.Error!);
        }

        var (listing, published, _, active) = gate.Value;

        if (active.Any(commitment => commitment.Kind == CommitmentKind.Sale))
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.PropertyCommitted);
        }

        if (active.Any(commitment => commitment.IsShortStay))
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.ShortStayOccupancy);
        }

        var request = new PurchaseRequest(
            Guid.NewGuid(),
            listing,
            acting.UserId,
            now,
            published.Id,
            command.OfferedAmount ?? published.Content.Amount,
            published.Content.Currency,
            command.Message);

        await repository.AddPurchaseRequestAsync(request, cancellationToken);

        if (await RequestCommit.SaveAsync(repository, cancellationToken) is { } conflict)
        {
            return Result<RequestCreatedResult>.Failure(conflict);
        }

        return Result<RequestCreatedResult>.Success(
            new RequestCreatedResult(RequestKind.Purchase, request.Id, listing.Id, request.Status.ToString(), request.SubmittedAt));
    }
}
