namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// A span of dates in which a rental can be booked: from <paramref name="StartDate"/> up to, but not including,
/// <paramref name="EndDate"/>, in the property's local calendar.
/// </summary>
public sealed record RentalWindow
{
    public RentalWindow(DateOnly startDate, DateOnly endDate)
    {
        if (endDate <= startDate)
        {
            throw new ArgumentException("A rental window must end after it starts.", nameof(endDate));
        }

        StartDate = startDate;
        EndDate = endDate;
    }

    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }

    /// <summary>True when the whole span from <paramref name="start"/> to <paramref name="end"/> lies inside this window.</summary>
    public bool Covers(DateOnly start, DateOnly end) => start >= StartDate && end <= EndDate;
}
