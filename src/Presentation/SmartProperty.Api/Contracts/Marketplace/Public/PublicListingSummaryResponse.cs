#nullable enable
namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// One listing as shown in public search results.
/// </summary>
/// <remarks>
/// PROVISIONAL public contract. <see cref="ListingId"/> is the only identifier and is the public resource
/// identifier; the canonical property id and every other internal identifier are intentionally absent.
///
/// The public values must eventually come from the listing's currently approved, published submission version.
/// A newer draft or pending submission must never replace or hide content that is already published. This record
/// therefore has no submission, version, status, owner, workspace, evidence or review member: it cannot say
/// which version it came from, and it cannot leak what it does not have.
///
/// <see cref="Purpose"/> and <see cref="PropertyType"/> are strings so that the public contract does not depend on
/// a Domain enum. Title and price are contract fields only; the commercial content that will supply them is not
/// defined yet.
/// </remarks>
public sealed record PublicListingSummaryResponse(
    Guid ListingId,
    string Purpose,
    string Title,
    PublicMoneyResponse Price,
    string PropertyType,
    PublicLocationResponse Location,
    DateTimeOffset PublishedAt);
