using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// The editable content of a listing draft, as a submitter supplies it.
/// </summary>
/// <remarks>
/// Only what a submitter may claim. The location and property type come from the registry, the legal authority from a
/// verified grant, the evidence versions and digests from the evidence provider, the rental's time zone from the
/// property's country, and every verification and scan result from the trusted sources. There is no member for any of
/// them, so a request has nowhere to put one. <see cref="EvidenceIds"/> may be empty on a draft; a submission
/// requires evidence.
/// <para>
/// A Sale states one price in <see cref="Amount"/> and <see cref="Currency"/>. A rental states
/// <see cref="PricingOptions"/> (one per pricing period it offers, each with its own amount and currency) and
/// <see cref="RentalTerms"/>; its plain price is then derived from the primary option, and
/// <see cref="Amount"/>, <see cref="Currency"/> and <see cref="RentPeriod"/> are not used. A rental given only a
/// single <see cref="RentPeriod"/> price, with neither options nor terms, is the form that predates options.
    /// <see cref="Negotiable"/> is a sale's only: whether its price is open to negotiation, or null when not stated.
    /// <see cref="AvailableFrom"/> is a sale's only too: the date the property is available from, today or later when saved.
/// </para>
/// </remarks>
public sealed record ListingDraftContentInput(
    string Title,
    string Description,
    decimal Amount,
    string Currency,
    RentPeriod? RentPeriod,
    IReadOnlyList<Guid> EvidenceIds,
    IReadOnlyList<RentPricingOptionInput>? PricingOptions = null,
    RentalTermsInput? RentalTerms = null,
    bool? Negotiable = null,
    DateOnly? AvailableFrom = null);

/// <summary>One offered pricing period with its own amount and currency, and the security deposit asked with it.</summary>
public sealed record RentPricingOptionInput(RentPeriod Period, decimal Amount, string Currency, decimal? Deposit = null);

/// <summary>A span of available dates, from <paramref name="StartDate"/> up to but not including <paramref name="EndDate"/>.</summary>
public sealed record RentalWindowInput(DateOnly StartDate, DateOnly EndDate);

/// <summary>
/// How a rental can be booked. There is no time zone here: it is the property's own, taken from its country.
/// </summary>
public sealed record RentalTermsInput(
    TimeOnly CheckInTime,
    TimeOnly CheckOutTime,
    IReadOnlyList<RentalWindowInput> Windows,
    int? MinimumStayDays = null);
