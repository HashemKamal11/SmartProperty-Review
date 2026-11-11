#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

public sealed record CreatePropertyRequest(
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
