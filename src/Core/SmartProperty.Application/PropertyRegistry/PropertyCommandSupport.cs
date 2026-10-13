using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry;

/// <summary>
/// The pieces every command that mutates a property shares, so update and archive stamp and report a change the
/// same way.
/// </summary>
internal static class PropertyCommandSupport
{
    /// <summary>
    /// The instant a real change is stamped with: the clock, read once and truncated to the microsecond precision
    /// PostgreSQL stores, so the value returned is exactly the value persisted and later read back. A clock behind
    /// the current update instant keeps that instant rather than moving backwards.
    /// </summary>
    public static DateTimeOffset ResolveUpdatedAt(
        IDateTimeProvider dateTimeProvider,
        DateTimeOffset currentUpdatedAt)
    {
        var now = dateTimeProvider.UtcNow;
        var truncated = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), now.Offset);

        return truncated >= currentUpdatedAt ? truncated : currentUpdatedAt;
    }

    /// <summary>The read model of the tracked aggregate, as the command left it.</summary>
    public static PropertyDetails ToDetails(Property property)
    {
        return new PropertyDetails(
            property.Id,
            property.Type,
            property.Status,
            new PropertyAddressDetails(
                property.Address.CountryCode,
                property.Address.City,
                property.Address.Region,
                property.Address.District,
                property.Address.AddressLine,
                property.Address.PostalCode,
                property.Address.Latitude,
                property.Address.Longitude),
            property.CreatedAt,
            property.UpdatedAt);
    }
}
