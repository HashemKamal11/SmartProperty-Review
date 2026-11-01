using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

internal sealed class PublicListingReadRepository(ApplicationDbContext context) : IPublicListingReadRepository
{
    // Fixed composable SQL. All caller values enter through parameterized LINQ predicates.
    // Value-converted Domain snapshots cannot safely be traversed through ordinary EF LINQ.
    private const string VisibleSql = """
        SELECT l.id AS "ListingId",
               (s.content_snapshot->'Payload'->>'Purpose')::integer AS "Purpose",
               s.content_snapshot->'Payload'->>'Title' AS "Title",
               s.content_snapshot->'Payload'->>'Description' AS "Description",
               (s.content_snapshot->'Payload'->>'Amount')::numeric AS "Amount",
               s.content_snapshot->'Payload'->>'Currency' AS "Currency",
               (s.content_snapshot->'Payload'->>'RentPeriod')::integer AS "RentPeriod",
               (s.content_snapshot->'Payload'->>'PropertyType')::integer AS "PropertyType",
               s.content_snapshot->'Payload'->>'Country' AS "Country",
               s.content_snapshot->'Payload'->>'Region' AS "Region",
               s.content_snapshot->'Payload'->>'City' AS "City",
               s.content_snapshot->'Payload'->>'District' AS "District",
               l.published_at AS "PublishedAt"
        FROM marketplace.listings AS l
        JOIN marketplace.submissions AS s
          ON s.id = l.published_submission_id AND s.listing_id = l.id
         AND s.property_id = l.property_id AND s.purpose = l.purpose
        JOIN registry.properties AS p ON p.id = l.property_id
        WHERE l.publication_status = 'Published' AND l.availability_status = 'Open'
          AND NOT l.closed_by_reservation AND l.published_at IS NOT NULL
          AND p.status = 'Active' AND s.status = 'Approved'
          AND s.content_snapshot->>'FormatVersion' = '1'
          AND EXISTS (SELECT 1 FROM marketplace.review_decisions AS d
                      WHERE d.submission_id = s.id AND d.approved)
          AND NOT EXISTS (SELECT 1 FROM marketplace.property_reservations AS r
                          WHERE r.property_id = l.property_id)
        """;

    private IQueryable<PublicListingReadRow> Visible() => context.Database.SqlQueryRaw<PublicListingReadRow>(VisibleSql);

    private IQueryable<PublicListingReadRow> Filter(PublicListingSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var comparesPrices = criteria.MinPrice.HasValue || criteria.MaxPrice.HasValue
            || criteria.Sort is PublicListingSortOrder.PriceAsc or PublicListingSortOrder.PriceDesc;
        if (!Enum.IsDefined(criteria.Sort)
            || (criteria.Purpose.HasValue && !Enum.IsDefined(criteria.Purpose.Value))
            || (criteria.PropertyType.HasValue && !Enum.IsDefined(criteria.PropertyType.Value))
            || (criteria.RentPeriod.HasValue && !Enum.IsDefined(criteria.RentPeriod.Value))
            || (criteria.RentPeriod.HasValue && criteria.Purpose != ListingPurpose.Rent)
            || criteria.MinPrice < 0 || criteria.MaxPrice < 0 || criteria.MinPrice > criteria.MaxPrice
            || (comparesPrices && (!criteria.Purpose.HasValue || string.IsNullOrWhiteSpace(criteria.Currency)))
            || (comparesPrices && criteria.Purpose == ListingPurpose.Rent && !criteria.RentPeriod.HasValue))
            throw new ArgumentException("Invalid public listing search criteria.", nameof(criteria));

        var query = Visible();
        if (criteria.Purpose is { } purpose) query = query.Where(x => x.Purpose == (int)purpose);
        if (criteria.PropertyType is { } type) query = query.Where(x => x.PropertyType == (int)type);
        if (criteria.RentPeriod is { } period) query = query.Where(x => x.RentPeriod == (int)period);
        // Never normalize stored historical strings or infer codes. C2 supplies normalized code filters.
        var country = Trim(criteria.CountryCode);
        var currency = Trim(criteria.Currency);
        var region = Trim(criteria.Region);
        var city = Trim(criteria.City);
        var district = Trim(criteria.District);
        if (country is not null) query = query.Where(x => EF.Functions.Collate(x.Country, "C") == country);
        if (currency is not null) query = query.Where(x => EF.Functions.Collate(x.Currency, "C") == currency);
        if (region is not null) query = query.Where(x => EF.Functions.Collate(x.Region, "C") == region);
        if (city is not null) query = query.Where(x => EF.Functions.Collate(x.City, "C") == city);
        if (district is not null) query = query.Where(x => EF.Functions.Collate(x.District, "C") == district);
        if (criteria.MinPrice is { } min) query = query.Where(x => x.Amount >= min);
        if (criteria.MaxPrice is { } max) query = query.Where(x => x.Amount <= max);
        return query;
    }

    private static IOrderedQueryable<PublicListingReadRow> Order(IQueryable<PublicListingReadRow> query, PublicListingSortOrder sort) => sort switch
    {
        PublicListingSortOrder.Newest => query.OrderByDescending(x => x.PublishedAt).ThenBy(x => x.ListingId),
        PublicListingSortOrder.PriceAsc => query.OrderBy(x => x.Amount).ThenBy(x => x.ListingId),
        PublicListingSortOrder.PriceDesc => query.OrderByDescending(x => x.Amount).ThenBy(x => x.ListingId),
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    public async Task<PublicListingPage> SearchAsync(PublicListingSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || skip > PageParameters.MaxOffset) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 1 || take > PageParameters.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(take));
        var query = Filter(criteria);
        // Two statements, not a repeatable-read snapshot: concurrent commits may change count/page membership.
        var count = await query.LongCountAsync(cancellationToken);
        var items = await Order(query, criteria.Sort).Skip(skip).Take(take)
            .Select(x => new PublicListingSummary(x.ListingId, (ListingPurpose)x.Purpose, x.Title,
                new PublicListingPrice(x.Amount, x.Currency, (RentPeriod?)x.RentPeriod), (PropertyType)x.PropertyType,
                new PublicListingLocation(x.Country, x.Region, x.City, x.District), x.PublishedAt))
            .ToListAsync(cancellationToken);
        return new PublicListingPage(items, count);
    }

    public Task<PublicListingDetail?> GetByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        Visible().Where(x => x.ListingId == listingId)
            .Select(x => new PublicListingDetail(x.ListingId, (ListingPurpose)x.Purpose, x.Title, x.Description,
                new PublicListingPrice(x.Amount, x.Currency, (RentPeriod?)x.RentPeriod), (PropertyType)x.PropertyType,
                new PublicListingLocation(x.Country, x.Region, x.City, x.District), x.PublishedAt))
            .SingleOrDefaultAsync(cancellationToken);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
