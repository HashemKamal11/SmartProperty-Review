namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>A stretch of dates, [Start, End): the end is the check-out day, so another stay may begin on it.</summary>
public sealed record PublicDateRange(DateOnly Start, DateOnly End);

/// <summary>
/// What a visitor needs to plan a stay: the check-in and check-out times in the property's own time zone, the dates
/// the owner offers, and the shortest stay. It names no owner and no authority.
/// </summary>
public sealed record PublicRentalTerms(
    string TimeZoneId,
    TimeOnly CheckInTime,
    TimeOnly CheckOutTime,
    int? MinimumStayDays,
    IReadOnlyList<PublicDateRange> Windows);
