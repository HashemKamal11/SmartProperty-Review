#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Owner;

/// <summary>
/// The commercial content of one advertisement. Nothing in it names a user, a party or an authority: those come from
/// the authenticated principal and the persisted grant, never from a request.
/// </summary>
/// <remarks>
/// For a Sale, <c>amount</c> and <c>currency</c> are the price. <c>negotiable</c> (true or false, optional) says whether
/// the sale price is open to negotiation; a Rent must not send it. <c>availableFrom</c> (a date, optional, today or later) is
/// when a sale property is available; a Rent uses its <c>rentalTerms.windows</c> instead. For a Rent, the price is given either as a single
/// <c>amount</c> with a <c>rentPeriod</c>, or as <c>pricingOptions</c>: any of Daily, Weekly, Monthly and Yearly, each
/// with its own amount and currency. The options are independent of how long a tenant stays; the length of a stay is a
/// request's own start and end dates. <c>rentalTerms</c> carries the check-in and check-out times and the availability
/// windows, in the property's local time.
/// </remarks>
public sealed record ListingDraftContentRequest(
    string? Title,
    string? Description,
    decimal? Amount,
    string? Currency,
    string? RentPeriod,
    IReadOnlyList<Guid>? EvidenceIds,
    IReadOnlyList<RentPricingOptionRequest>? PricingOptions = null,
    RentalTermsRequest? RentalTerms = null,
    bool? Negotiable = null,
    DateOnly? AvailableFrom = null);

/// <summary>One rent pricing row: the period, its amount and currency, and optionally the security deposit asked with it (same currency, zero or more).</summary>
public sealed record RentPricingOptionRequest(string? Period, decimal? Amount, string? Currency, decimal? Deposit = null);

public sealed record RentalWindowRequest(DateOnly? StartDate, DateOnly? EndDate);

public sealed record RentalTermsRequest(
    TimeOnly? CheckInTime,
    TimeOnly? CheckOutTime,
    IReadOnlyList<RentalWindowRequest>? Windows,
    int? MinimumStayDays = null);

public sealed record CreateListingDraftRequest(
    Guid? PropertyId,
    string? Purpose,
    ListingDraftContentRequest? Content,
    string? RelistMode = null);

/// <summary>Advertises one property for sale, for rent, or both. Either side may be omitted, not both.</summary>
public sealed record CreatePropertyListingDraftsRequest(
    ListingDraftContentRequest? Sale,
    ListingDraftContentRequest? Rent,
    string? RelistMode = null);

public sealed record UpdateListingDraftRequest(int? ExpectedRevision, ListingDraftContentRequest? Content);

public sealed record SubmitListingDraftRequest(int? ExpectedRevision);

public sealed record ListingDraftResponse(Guid SubmissionId, Guid ListingId, int Version, int Revision);

public sealed record PropertyListingDraftsResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ListingDraftResponse? Sale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ListingDraftResponse? Rent);

public sealed record SubmitListingDraftResponse(Guid SubmissionId, string Status, DateTimeOffset SubmittedAt);

public sealed record DiscardListingDraftResponse(bool ListingRemoved);

public sealed record WithdrawListingResponse(Guid ListingId, DateTimeOffset WithdrawnAt);

/// <summary>"Manual" (the default: the listing stays closed until the owner relists it) or "Automatic".</summary>
public sealed record SetRelistModeRequest(string? Mode);

public sealed record RelistModeResponse(Guid ListingId, string Mode);

/// <summary>The copy that relisting created: a new version already submitted for administrator review.</summary>
public sealed record RelistListingResponse(Guid SubmissionId, Guid ListingId, int Version, string Status, DateTimeOffset SubmittedAt);
