using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.PublicListings.GetById;

public sealed class GetPublicListingByIdQueryHandler(IPublicListingReadRepository publicListings)
    : IQueryHandler<GetPublicListingByIdQuery, PublicListingDetail>
{
    public async Task<Result<PublicListingDetail>> Handle(
        GetPublicListingByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No listing can have the empty id, so asking the database would only confirm what is already known.
        if (query.ListingId == Guid.Empty)
        {
            return Result<PublicListingDetail>.Failure(PublicListingErrors.NotFound);
        }

        var listing = await publicListings.GetByIdAsync(query.ListingId, cancellationToken);

        // One answer for every listing that cannot be shown publicly: the caller cannot tell a missing listing
        // from an unpublished, withdrawn, reserved or unapproved one.
        return listing is null
            ? Result<PublicListingDetail>.Failure(PublicListingErrors.NotFound)
            : Result<PublicListingDetail>.Success(listing);
    }
}
