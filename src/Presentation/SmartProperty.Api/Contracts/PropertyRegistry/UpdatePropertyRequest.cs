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
    Guid? AreaId,
    string? AddressLine = null,
    string? PostalCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? Bedrooms = null,
    int? Bathrooms = null,
    DateOnly? BuiltDate = null,
    string? FinishingStatus = null,
    IReadOnlyList<string>? AmenityCodes = null,
    IReadOnlyList<string>? ViewCodes = null,
    decimal? AreaSqm = null,
    int? Floor = null,
    bool? Furnished = null,
    string? TimeZoneId = null,
    Guid? CompoundId = null)
{
    /// <summary>Members the model did not bind. Only used to refuse the commercial fields that moved to listings.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? UnmatchedMembers { get; init; }
}
