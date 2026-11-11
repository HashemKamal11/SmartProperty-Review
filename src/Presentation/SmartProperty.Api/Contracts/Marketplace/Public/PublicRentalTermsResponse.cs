#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>A stretch of dates, <c>start</c> up to <c>end</c>; the end is the check-out day, so another stay may begin on it.</summary>
public sealed record PublicDateRangeResponse(string Start, string End);

/// <summary>
/// What a visitor needs to plan a stay. The check-in and check-out times are in the property's own time zone;
/// <c>windows</c> are the dates the owner offers; <c>minimumStayDays</c> is omitted when there is no minimum.
/// </summary>
public sealed record PublicRentalTermsResponse(
    string TimeZoneId,
    string CheckInTime,
    string CheckOutTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MinimumStayDays,
    IReadOnlyList<PublicDateRangeResponse> Windows);
