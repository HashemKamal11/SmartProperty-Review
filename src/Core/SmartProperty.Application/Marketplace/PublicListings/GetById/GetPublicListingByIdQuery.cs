using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Marketplace.PublicListings.GetById;

/// <summary>A public, anonymous request for one listing by its public identifier.</summary>
public sealed record GetPublicListingByIdQuery(Guid ListingId, Guid? ViewerUserId = null) : IQuery<PublicListingDetail>;
