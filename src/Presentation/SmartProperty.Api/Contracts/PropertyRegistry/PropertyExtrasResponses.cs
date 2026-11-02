#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>One view. Send <c>code</c> in <c>viewCodes</c> when creating or updating a property.</summary>
public sealed record ViewResponse(string Code, string NameAr, string NameEn);

/// <summary>
/// The market-facing facts of a property. Where it is lives in <c>place</c>.
/// </summary>
public sealed record PropertyListingResponse(
    string? TransactionType,
    decimal? Price,
    decimal? AreaSqm,
    int? Floor,
    bool? Furnished,
    string ListingStatus,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn);

/// <summary>
/// Where a property is. Choose it with <c>areaId</c> (and <c>compoundId</c>) from <c>GET /api/locations</c>; the
/// city and country above the area are filled in.
/// </summary>
public sealed record PropertyPlaceResponse(
    PlaceResponseItem Country,
    PlaceResponseItem City,
    PlaceResponseItem Area,
    PlaceResponseItem? Compound);

public sealed record PlaceResponseItem(Guid Id, string NameEn, string NameAr);

/// <summary>
/// Whether the AI search service holds the property. <c>status</c> is <c>Synced</c>, <c>NotSynced</c>, or
/// <c>Failed</c>; <c>error</c> says what is missing or what went wrong.
/// </summary>
public sealed record AiSyncResponse(string Status, DateTimeOffset? SyncedAt, string? Error);
