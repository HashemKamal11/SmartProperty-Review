#nullable enable
namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// One listing as shown on its public detail page: every summary field plus the description.
/// </summary>
/// <remarks>
/// PROVISIONAL public contract, kept as its own record so the summary and the detail can evolve independently.
/// The privacy rules of <see cref="PublicListingSummaryResponse"/> apply unchanged: <see cref="ListingId"/> is the
/// only identifier, nothing private has a member here, and the values must eventually come from the currently
/// approved, published submission version, never from a newer draft.
///
/// Title, description and price are contract fields only; the commercial content that will supply them is not
/// defined yet.
/// </remarks>
public sealed record PublicListingDetailResponse(
    Guid ListingId,
    string Purpose,
    string Title,
    string Description,
    PublicMoneyResponse Price,
    string PropertyType,
    PublicLocationResponse Location,
    DateTimeOffset PublishedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PublicMoneyResponse>? PricingOptions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PublicRentalTermsResponse? RentalTerms = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PublicDateRangeResponse>? BlockedRanges = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? PropertyId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CoverImageUrl = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Bedrooms = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Bathrooms = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? AreaSqm = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MatchPercentage = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? IsFavorite = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? PricePerSqm = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PublicPropertyFactsResponse? Property = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PublicListingSummaryResponse>? OtherListings = null);
