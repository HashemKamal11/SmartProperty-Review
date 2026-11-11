namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// When and how a rental can be booked: the property's local time zone, the check-in and check-out times, an optional
/// minimum stay, and the windows in which dates can be requested. Immutable.
/// </summary>
/// <remarks>
/// The time zone is the property's own, never a client's choice, so a date means the same thing to the owner and the
/// renter. It is held as an IANA identifier and checked against the platform's time zone data.
/// </remarks>
public sealed class RentalTerms
{
    public const int MaxWindows = 50;
    public const int MaxMinimumStayDays = 3650;

    public RentalTerms(
        string timeZoneId,
        TimeOnly checkInTime,
        TimeOnly checkOutTime,
        IEnumerable<RentalWindow> windows,
        int? minimumStayDays = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        ArgumentNullException.ThrowIfNull(windows);

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            throw new ArgumentException("The time zone is not a known time zone identifier.", nameof(timeZoneId));
        }

        if (minimumStayDays is < 1 or > MaxMinimumStayDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumStayDays), minimumStayDays, $"The minimum stay must be between 1 and {MaxMinimumStayDays} days.");
        }

        var ordered = windows.OrderBy(window => window?.StartDate).ToArray();

        if (ordered.Length == 0 || ordered.Any(window => window is null))
        {
            throw new ArgumentException("A rental needs at least one window of available dates.", nameof(windows));
        }

        if (ordered.Length > MaxWindows)
        {
            throw new ArgumentException($"A rental has at most {MaxWindows} windows.", nameof(windows));
        }

        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].StartDate < ordered[i - 1].EndDate)
            {
                throw new ArgumentException("Rental windows must not overlap.", nameof(windows));
            }
        }

        TimeZoneId = timeZoneId;
        CheckInTime = checkInTime;
        CheckOutTime = checkOutTime;
        MinimumStayDays = minimumStayDays;
        Windows = Array.AsReadOnly(ordered);
    }

    public string TimeZoneId { get; }
    public TimeOnly CheckInTime { get; }
    public TimeOnly CheckOutTime { get; }
    public int? MinimumStayDays { get; }

    /// <summary>The available windows, earliest first, never overlapping.</summary>
    public IReadOnlyList<RentalWindow> Windows { get; }
}
