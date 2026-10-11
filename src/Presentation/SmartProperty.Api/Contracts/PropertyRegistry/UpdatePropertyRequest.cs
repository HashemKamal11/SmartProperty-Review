#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>
/// Full replacement of every mutable property detail. PUT semantics: an optional field that is omitted binds as
/// null and therefore clears the stored value, exactly as an explicit null does. Send the complete current state
/// with the corrections applied, never only the fields that changed.
/// </summary>
/// <remarks>
/// The id comes from the route alone. Status, creation time, and update time are server-controlled and have no
/// field here, so a body that names them changes nothing.
/// </remarks>
public sealed record UpdatePropertyRequest(
    string? Type,
    string? CountryCode,
    string? City = null,
    string? Region = null,
    string? District = null,
    string? AddressLine = null,
    string? PostalCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null);
