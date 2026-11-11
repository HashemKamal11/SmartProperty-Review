using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Requests.Create;

/// <summary>
/// Makes a dated rental request against the approved version of a Rent listing.
/// </summary>
/// <remarks>
/// <para>
/// The customer is the authenticated, Active user and never a request value. The price comes from the chosen pricing
/// option of the approved version, and the check-in and check-out times and the time zone from its rental terms, so
/// nothing the customer sends can change what they will pay or when they check in.
/// </para>
/// <para>
/// A request is accepted only if the stay lies inside one of the availability windows, meets the minimum stay, starts
/// no earlier than today in the property's own time zone, and does not overlap a commitment that is in force. Other
/// pending requests may overlap each other; which one wins is the owner's decision when they accept.
/// </para>
/// </remarks>
public sealed class CreateRentalRequestCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMarketplaceRequestWriteRepository repository,
    IDateTimeProvider clock)
    : ICommandHandler<CreateRentalRequestCommand, RequestCreatedResult>
{
    public async Task<Result<RequestCreatedResult>> Handle(
        CreateRentalRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<RequestCreatedResult>.Failure(accountError);
        }

        if (command.ListingId == Guid.Empty
            || !Enum.IsDefined(command.PricingPeriod)
            || command.EndDate <= command.StartDate
            || command.EndDate.DayNumber - command.StartDate.DayNumber > RentalRequest.MaxStayDays
            || command.Message?.Trim().Length > RentalRequest.MaxMessageLength)
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.Invalid(
                "A listing, a pricing period, and a start date before the end date are required, and the message is at most 1000 characters."));
        }

        var now = clock.UtcNow;
        var gate = await RequestableListingGate.OpenAsync(
            repository, acting.UserId, command.ListingId, ListingPurpose.Rent, now, cancellationToken);

        if (gate.IsFailure)
        {
            return Result<RequestCreatedResult>.Failure(gate.Error!);
        }

        var (listing, published, _, active) = gate.Value;
        var content = published.Content;
        var terms = content.RentalTerms;

        if (terms is null)
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.RentalTermsMissing);
        }

        var pricing = ChoosePricing(content, command.PricingPeriod);

        if (pricing is null)
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.PricingOptionUnavailable);
        }

        var rule = CheckStay(terms, command.StartDate, command.EndDate, now);

        if (rule is not null)
        {
            return Result<RequestCreatedResult>.Failure(rule);
        }

        if (active.Any(commitment => commitment.Kind == CommitmentKind.Sale))
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.PropertyCommitted);
        }

        if (active.Any(commitment => commitment.Overlaps(command.StartDate, command.EndDate)))
        {
            return Result<RequestCreatedResult>.Failure(RequestErrors.DatesUnavailable);
        }

        var request = new RentalRequest(
            Guid.NewGuid(),
            listing,
            acting.UserId,
            now,
            published.Id,
            pricing,
            command.StartDate,
            command.EndDate,
            terms.CheckInTime,
            terms.CheckOutTime,
            terms.TimeZoneId,
            command.Message);

        await repository.AddRentalRequestAsync(request, cancellationToken);

        if (await RequestCommit.SaveAsync(repository, cancellationToken) is { } conflict)
        {
            return Result<RequestCreatedResult>.Failure(conflict);
        }

        return Result<RequestCreatedResult>.Success(
            new RequestCreatedResult(RequestKind.Rental, request.Id, listing.Id, request.Status.ToString(), request.SubmittedAt));
    }

    private static RentPricingOption? ChoosePricing(ListingSubmissionContent content, RentPeriod period)
    {
        if (content.PricingOptions.Count > 0)
        {
            return content.PricingOptions.FirstOrDefault(option => option.Period == period);
        }

        // A rental stated with a single price offers exactly that period.
        return content.RentPeriod == period ? new RentPricingOption(period, content.Amount, content.Currency) : null;
    }

    private static Error? CheckStay(RentalTerms terms, DateOnly start, DateOnly end, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(terms.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        if (start < today)
        {
            return RequestErrors.Invalid("A rental cannot start before today in the property's local time.");
        }

        if (!terms.Windows.Any(window => window.Covers(start, end)))
        {
            return RequestErrors.Invalid("The stay must fall entirely inside one of the listing's availability windows.");
        }

        if (terms.MinimumStayDays is { } minimum && end.DayNumber - start.DayNumber < minimum)
        {
            return RequestErrors.Invalid($"The listing requires a stay of at least {minimum} days.");
        }

        return null;
    }
}
