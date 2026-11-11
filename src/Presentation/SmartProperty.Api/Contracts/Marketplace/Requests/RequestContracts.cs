#nullable enable
using System.Text.Json.Serialization;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.Requests.Settings;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Contracts.Marketplace.Requests;

/// <summary>An offer to buy. The buyer is the signed-in user; the offer defaults to the listing's price.</summary>
public sealed record CreatePurchaseRequestRequest(Guid? ListingId, decimal? OfferedAmount, string? Message);

/// <summary>
/// A request to rent for dates. There is no price: it is copied from the listing's pricing option for
/// <c>pricingPeriod</c> (Daily, Weekly, Monthly or Yearly). The stay is <c>startDate</c> up to <c>endDate</c>, which is
/// the check-out day; both are in the property's own calendar.
/// </summary>
public sealed record CreateRentalRequestRequest(
    Guid? ListingId, string? PricingPeriod, DateOnly? StartDate, DateOnly? EndDate, string? Message);

public sealed record SetSigningWindowRequest(int? Hours);

public sealed record RequestCreatedResponse(string Kind, Guid RequestId, Guid ListingId, string Status, DateTimeOffset SubmittedAt);

public sealed record RequestDecisionResponse(string Kind, Guid RequestId, string Status, DateTimeOffset DecidedAt);

public sealed record AcceptRequestResponse(
    string Kind, Guid RequestId, Guid CommitmentId, string Status, DateTimeOffset AcceptedAt, DateTimeOffset SigningDeadline);

public sealed record CommitmentStepResponse(Guid CommitmentId, string Status, DateTimeOffset At);

public sealed record SigningWindowResponse(
    int Hours,
    bool IsDefault,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UpdatedAt);

/// <summary>
/// One request. A purchase has no dates; a request from before dated rentals has neither dates nor price. The requester
/// is named only to the advertiser who received the request.
/// </summary>
public sealed record MarketplaceRequestResponse(
    string Kind,
    Guid RequestId,
    Guid ListingId,
    string Status,
    DateTimeOffset SubmittedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? DecidedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PricingPeriod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? StartDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? EndDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeOnly? CheckInTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeOnly? CheckOutTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZoneId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequesterName);

public sealed record CommitmentResponse(
    Guid CommitmentId,
    string Kind,
    string Status,
    Guid ListingId,
    decimal Amount,
    string Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PricingPeriod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? StartDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? EndDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeOnly? CheckInTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeOnly? CheckOutTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZoneId,
    DateTimeOffset AcceptedAt,
    DateTimeOffset SigningDeadline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SignedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? HandedOverAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CompletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiredAt,
    bool IsOwner);

internal static class RequestContractMapper
{
    public static Error Invalid(string description) => new("validation.failed", description, ErrorType.Validation);

    // Names only: a number never selects an enum member.
    public static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;

        return !string.IsNullOrWhiteSpace(text)
            && !char.IsDigit(text.Trim()[0])
            && Enum.TryParse(text.Trim(), ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    public static MarketplaceRequestResponse ToResponse(MarketplaceRequestView view) => new(
        view.Kind.ToString(), view.RequestId, view.ListingId, view.Status, view.SubmittedAt, view.DecidedAt, view.Amount,
        view.Currency, view.PricingPeriod, view.StartDate, view.EndDate, view.CheckInTime, view.CheckOutTime, view.TimeZoneId,
        view.Message, view.RequesterName);

    public static CommitmentResponse ToResponse(CommitmentView view) => new(
        view.CommitmentId, view.Kind, view.Status, view.ListingId, view.Amount, view.Currency, view.PricingPeriod,
        view.StartDate, view.EndDate, view.CheckInTime, view.CheckOutTime, view.TimeZoneId, view.AcceptedAt,
        view.SigningDeadline, view.SignedAt, view.HandedOverAt, view.CompletedAt, view.ExpiredAt, view.IsOwner);

    public static RequestDecisionResponse ToResponse(RequestDecisionResult result) => new(
        result.Kind.ToString(), result.RequestId, result.Status, result.DecidedAt);

    public static AcceptRequestResponse ToResponse(AcceptRequestResult result) => new(
        result.Kind.ToString(), result.RequestId, result.CommitmentId, result.Status, result.AcceptedAt, result.SigningDeadline);

    public static SigningWindowResponse ToResponse(SigningWindowResult result) => new(result.Hours, result.IsDefault, result.UpdatedAt);
}
