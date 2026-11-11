namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// The physical facts of a property that do not fit the other registry fields: how big it is, which floor it is on,
/// whether it is furnished, and the time zone it is in. All of them are optional.
/// </summary>
/// <remarks>
/// Immutable and compared by value, like <see cref="PropertyAddress"/>. Commercial and advertisement data (the
/// transaction type, prices, availability, and promotional titles and descriptions) is deliberately not here: it
/// belongs to the Marketplace listing, so one physical property is never duplicated to advertise it for sale and
/// for rent.
/// <para>
/// <see cref="TimeZoneId"/> is where the property is, so it is a physical fact. It is optional because most
/// properties simply share their country's single time zone; it exists for a property whose country has several
/// zones, or none recorded, and it always wins over the country's.
/// </para>
/// </remarks>
public sealed record PropertyPhysicalInfo
{
    public const decimal MaxAreaSqm = 100000m;
    public const int MinFloor = -5;
    public const int MaxFloor = 300;
    public const int MaxTimeZoneIdLength = 64;

    public static readonly PropertyPhysicalInfo Empty = new();

    private PropertyPhysicalInfo()
    {
    }

    public PropertyPhysicalInfo(decimal? areaSqm = null, int? floor = null, bool? furnished = null, string? timeZoneId = null)
    {
        if (areaSqm is <= 0m or > MaxAreaSqm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(areaSqm), areaSqm, $"Area must be greater than zero and at most {MaxAreaSqm} square metres.");
        }

        if (floor is < MinFloor or > MaxFloor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(floor), floor, $"Floor must be between {MinFloor} and {MaxFloor}.");
        }

        var zone = string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId.Trim();

        if (zone is not null
            && (zone.Length > MaxTimeZoneIdLength || !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _)))
        {
            throw new ArgumentException("The time zone is not a known time zone identifier.", nameof(timeZoneId));
        }

        AreaSqm = areaSqm;
        Floor = floor;
        Furnished = furnished;
        TimeZoneId = zone;
    }

    public decimal? AreaSqm { get; private set; }
    public int? Floor { get; private set; }
    public bool? Furnished { get; private set; }

    /// <summary>The property's own IANA time zone, or null when it shares its country's.</summary>
    public string? TimeZoneId { get; private set; }
}
