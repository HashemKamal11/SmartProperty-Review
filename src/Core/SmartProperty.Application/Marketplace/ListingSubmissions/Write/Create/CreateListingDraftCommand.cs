using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;

/// <summary>
/// Creates a draft listing submission for a property and purpose, or the next revision of an already reviewed one.
/// </summary>
/// <remarks>
/// A contract only; no handler exists and none is registered. Who is asking is never a request input: the handler
/// will read the acting user from the authenticated principal and will allocate nothing until a verified grant has
/// been evaluated, and evaluated again under the property lock. There is no field for a legal authority, an owner, a
/// verification result, a scan result or a reviewer.
/// </remarks>
public sealed record CreateListingDraftCommand(
    Guid PropertyId,
    ListingPurpose Purpose,
    ListingDraftContentInput Content) : ICommand<ListingDraftResult>;
