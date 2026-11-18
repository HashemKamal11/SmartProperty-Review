using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

internal sealed class PublicListingReadRepository(ApplicationDbContext context) : IPublicListingReadRepository, IPropertyListingsLookup
{
    // Fixed composable SQL. Every caller value enters through a parameter (the requested pricing period and the
    // requested stay) or through parameterized LINQ predicates; nothing is concatenated into the text.
    // Value-converted Domain snapshots cannot safely be traversed through ordinary EF LINQ, so the few members that
    // are public are projected here by name.
    //
    // Visibility. A listing is shown only while it is published and open, its approved version is the one it
    // publishes, its property is active and not held by a legacy reservation. Closing a listing (a withdrawal, or the
    // acceptance of a request) takes it out of this query at once. Dates held by a commitment are reported as blocked,
    // and an unsigned commitment past its signing deadline no longer holds anything, whether or not it was stored as
    // expired yet.
    //
    // The viewer, when signed in, adds their own latest match percentage and favourite flag; for a visitor both are
    // NULL. Bedrooms, bathrooms, area and the cover photo are read live from the registry, so the card always shows
    // the property as it is; the place names are read only to filter and search.
    private IQueryable<PublicListingReadRow> Visible(int? period, DateOnly? availableFrom, DateOnly? availableTo, Guid? viewer = null) =>
        context.Database.SqlQuery<PublicListingReadRow>(
            $"""
            SELECT l.id AS "ListingId",
                   l.property_id AS "PropertyId",
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
                   l.published_at AS "PublishedAt",
                   CASE WHEN {period}::integer IS NULL THEN NULL ELSE COALESCE(
                       (SELECT (o->>'Amount')::numeric
                          FROM jsonb_array_elements(COALESCE(s.content_snapshot->'Payload'->'PricingOptions', '[]'::jsonb)) AS o
                         WHERE (o->>'Period')::integer = {period}::integer LIMIT 1),
                       CASE WHEN jsonb_typeof(s.content_snapshot->'Payload'->'PricingOptions') IS DISTINCT FROM 'array'
                                 AND (s.content_snapshot->'Payload'->>'RentPeriod')::integer = {period}::integer
                            THEN (s.content_snapshot->'Payload'->>'Amount')::numeric END) END AS "PeriodAmount",
                   CASE WHEN {period}::integer IS NULL THEN NULL ELSE COALESCE(
                       (SELECT o->>'Currency'
                          FROM jsonb_array_elements(COALESCE(s.content_snapshot->'Payload'->'PricingOptions', '[]'::jsonb)) AS o
                         WHERE (o->>'Period')::integer = {period}::integer LIMIT 1),
                       CASE WHEN jsonb_typeof(s.content_snapshot->'Payload'->'PricingOptions') IS DISTINCT FROM 'array'
                                 AND (s.content_snapshot->'Payload'->>'RentPeriod')::integer = {period}::integer
                            THEN s.content_snapshot->'Payload'->>'Currency' END) END AS "PeriodCurrency",
                   (s.content_snapshot->'Payload'->'PricingOptions')::text AS "PricingOptionsJson",
                   (s.content_snapshot->'Payload'->'RentalTerms')::text AS "RentalTermsJson",
                   (SELECT string_agg(c.start_date::text || '|' || c.end_date::text, ',' ORDER BY c.start_date)
                      FROM marketplace.property_commitments AS c
                     WHERE c.property_id = l.property_id AND c.kind = 'Rental'
                       AND c.status IN ('PendingSignature', 'Signed', 'Occupied')
                       AND NOT (c.status = 'PendingSignature' AND c.signing_deadline <= now())) AS "BlockedRangesText",
                   p.bedrooms AS "Bedrooms",
                   p.bathrooms AS "Bathrooms",
                   p.area_sqm AS "AreaSqm",
                   (SELECT i.id FROM registry.property_images AS i
                     WHERE i.property_id = l.property_id
                     ORDER BY i.sort_order, i.created_at, i.id LIMIT 1) AS "CoverImageId",
                   p.area_id AS "AreaId",
                   ar.city_id AS "CityId",
                   p.compound_id AS "CompoundId",
                   ar.name_en AS "AreaNameEn",
                   ar.name_ar AS "AreaNameAr",
                   ci.name_en AS "CityNameEn",
                   ci.name_ar AS "CityNameAr",
                   co.name_en AS "CountryNameEn",
                   co.name_ar AS "CountryNameAr",
                   cp.name_en AS "CompoundNameEn",
                   cp.name_ar AS "CompoundNameAr",
                   CASE WHEN {viewer}::uuid IS NULL THEN NULL ELSE
                       (SELECT m.match_percentage FROM matching.property_matches AS m
                         WHERE m.user_id = {viewer}::uuid AND m.property_id = l.property_id
                         ORDER BY m.created_at DESC, m.id DESC LIMIT 1) END AS "MatchPercentage",
                   CASE WHEN {viewer}::uuid IS NULL THEN NULL ELSE
                       EXISTS (SELECT 1 FROM registry.favorite_properties AS f
                                WHERE f.user_id = {viewer}::uuid AND f.property_id = l.property_id) END AS "IsFavorite"
            FROM marketplace.listings AS l
            JOIN marketplace.submissions AS s
              ON s.id = l.published_submission_id AND s.listing_id = l.id
             AND s.property_id = l.property_id AND s.purpose = l.purpose
            JOIN registry.properties AS p ON p.id = l.property_id
            LEFT JOIN locations.areas AS ar ON ar.id = p.area_id
            LEFT JOIN locations.cities AS ci ON ci.id = ar.city_id
            LEFT JOIN locations.countries AS co ON co.id = ci.country_id
            LEFT JOIN locations.compounds AS cp ON cp.id = p.compound_id
            WHERE l.publication_status = 'Published' AND l.availability_status = 'Open'
              AND NOT l.closed_by_reservation AND l.published_at IS NOT NULL
              AND p.status = 'Active' AND s.status = 'Approved'
              AND s.content_snapshot->>'FormatVersion' = '1'
              AND EXISTS (SELECT 1 FROM marketplace.review_decisions AS d
                          WHERE d.submission_id = s.id AND d.approved)
              AND NOT EXISTS (SELECT 1 FROM marketplace.property_reservations AS r
                              WHERE r.property_id = l.property_id)
              AND ({availableFrom}::date IS NULL OR (
                    EXISTS (SELECT 1
                              FROM jsonb_array_elements(COALESCE(s.content_snapshot->'Payload'->'RentalTerms'->'Windows', '[]'::jsonb)) AS w
                             WHERE (w->>'StartDate')::date <= {availableFrom}::date AND (w->>'EndDate')::date >= {availableTo}::date)
                    AND NOT EXISTS (SELECT 1
                                      FROM marketplace.property_commitments AS c
                                     WHERE c.property_id = l.property_id AND c.kind = 'Rental'
                                       AND c.status IN ('PendingSignature', 'Signed', 'Occupied')
                                       AND NOT (c.status = 'PendingSignature' AND c.signing_deadline <= now())
                                       AND c.start_date < {availableTo}::date AND {availableFrom}::date < c.end_date)))
            """);

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
            || (comparesPrices && criteria.Purpose == ListingPurpose.Rent && !criteria.RentPeriod.HasValue)
            || (criteria.AvailableFrom.HasValue != criteria.AvailableTo.HasValue)
            || (criteria.AvailableFrom.HasValue && (criteria.AvailableTo <= criteria.AvailableFrom || criteria.Purpose != ListingPurpose.Rent))
            || (criteria.PropertyTypes?.Any(candidate => !Enum.IsDefined(candidate)) ?? false)
            || criteria.MinBedrooms > criteria.MaxBedrooms
            || ((criteria.MinMatchPercentage.HasValue || criteria.Sort == PublicListingSortOrder.Match) && criteria.ViewerUserId is null))
            throw new ArgumentException("Invalid public listing search criteria.", nameof(criteria));

        var query = Visible(
            criteria.RentPeriod is { } requested ? (int)requested : null,
            criteria.AvailableFrom,
            criteria.AvailableTo,
            criteria.ViewerUserId);
        if (criteria.Purpose is { } purpose) query = query.Where(x => x.Purpose == (int)purpose);
        if (criteria.PropertyType is { } type) query = query.Where(x => x.PropertyType == (int)type);
        if (criteria.PropertyTypes is { Count: > 0 } types)
        {
            var wanted = types.Select(candidate => (int)candidate).ToArray();
            query = query.Where(x => wanted.Contains(x.PropertyType));
        }

        if (criteria.AreaId is { } areaId) query = query.Where(x => x.AreaId == areaId);
        if (criteria.CityId is { } cityId) query = query.Where(x => x.CityId == cityId);
        if (criteria.CompoundId is { } compoundId) query = query.Where(x => x.CompoundId == compoundId);
        if (criteria.MinBedrooms is { } fewest) query = query.Where(x => x.Bedrooms >= fewest);
        if (criteria.MaxBedrooms is { } most) query = query.Where(x => x.Bedrooms <= most);
        if (criteria.MinMatchPercentage is { } minMatch) query = query.Where(x => x.MatchPercentage >= minMatch);

        // Every word must appear in the listing's own text or in a place name, in English or Arabic.
        foreach (var term in criteria.SearchTerms ?? [])
        {
            var pattern = LikePattern(term);
            query = query.Where(x =>
                EF.Functions.ILike(x.Title, pattern, LikeEscape)
                || EF.Functions.ILike(x.Description, pattern, LikeEscape)
                || EF.Functions.ILike(x.City, pattern, LikeEscape)
                || EF.Functions.ILike(x.Region, pattern, LikeEscape)
                || EF.Functions.ILike(x.District, pattern, LikeEscape)
                || EF.Functions.ILike(x.AreaNameEn ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.AreaNameAr ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.CityNameEn ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.CityNameAr ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.CountryNameEn ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.CountryNameAr ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.CompoundNameEn ?? "", pattern, LikeEscape)
                || EF.Functions.ILike(x.CompoundNameAr ?? "", pattern, LikeEscape));
        }

        // A rental is found by any pricing period it offers, not only its primary one, and its price in that period is
        // the price that is compared, sorted and shown. A search without a period compares the plain listing price.
        var byPeriod = criteria.RentPeriod.HasValue;
        if (byPeriod) query = query.Where(x => x.PeriodAmount != null);

        // Never normalize stored historical strings or infer codes. C2 supplies normalized code filters.
        var country = Trim(criteria.CountryCode);
        var currency = Trim(criteria.Currency);
        var region = Trim(criteria.Region);
        var city = Trim(criteria.City);
        var district = Trim(criteria.District);
        if (country is not null) query = query.Where(x => EF.Functions.Collate(x.Country, "C") == country);
        if (currency is not null)
        {
            query = byPeriod
                ? query.Where(x => EF.Functions.Collate(x.PeriodCurrency!, "C") == currency)
                : query.Where(x => EF.Functions.Collate(x.Currency, "C") == currency);
        }

        if (region is not null) query = query.Where(x => EF.Functions.Collate(x.Region, "C") == region);
        if (city is not null) query = query.Where(x => EF.Functions.Collate(x.City, "C") == city);
        if (district is not null)
        {
            // The listing's own district text exactly, or the property's area by its current English or Arabic name
            // (any case), so a district picked from the places list finds its listings in either language.
            var lowered = district.ToLowerInvariant();
            query = query.Where(x => EF.Functions.Collate(x.District, "C") == district
                || (x.AreaNameEn != null && x.AreaNameEn.ToLower() == lowered)
                || (x.AreaNameAr != null && x.AreaNameAr.ToLower() == lowered));
        }
        if (criteria.MinPrice is { } min) query = byPeriod ? query.Where(x => x.PeriodAmount >= min) : query.Where(x => x.Amount >= min);
        if (criteria.MaxPrice is { } max) query = byPeriod ? query.Where(x => x.PeriodAmount <= max) : query.Where(x => x.Amount <= max);
        return query;
    }

    private static IOrderedQueryable<PublicListingReadRow> Order(
        IQueryable<PublicListingReadRow> query, PublicListingSortOrder sort, bool byPeriod) => sort switch
    {
        PublicListingSortOrder.Newest => query.OrderByDescending(x => x.PublishedAt).ThenBy(x => x.ListingId),
        PublicListingSortOrder.PriceAsc => byPeriod
            ? query.OrderBy(x => x.PeriodAmount).ThenBy(x => x.ListingId)
            : query.OrderBy(x => x.Amount).ThenBy(x => x.ListingId),
        PublicListingSortOrder.PriceDesc => byPeriod
            ? query.OrderByDescending(x => x.PeriodAmount).ThenBy(x => x.ListingId)
            : query.OrderByDescending(x => x.Amount).ThenBy(x => x.ListingId),
        PublicListingSortOrder.Match => query
            .OrderByDescending(x => x.MatchPercentage ?? -1m)
            .ThenByDescending(x => x.PublishedAt)
            .ThenBy(x => x.ListingId),
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    public async Task<PublicListingPage> SearchAsync(PublicListingSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || skip > PageParameters.MaxOffset) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 1 || take > PageParameters.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(take));
        var query = Filter(criteria);
        var byPeriod = criteria.RentPeriod.HasValue;
        // Two statements, not a repeatable-read snapshot: concurrent commits may change count/page membership.
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await Order(query, criteria.Sort, byPeriod).Skip(skip).Take(take).ToListAsync(cancellationToken);
        var items = rows.Select(row => ToSummary(row, criteria.RentPeriod)).ToList();
        return new PublicListingPage(items, count);
    }

    public Task<PublicListingDetail?> GetByIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        GetByIdAsync(listingId, viewerUserId: null, cancellationToken);

    public async Task<PublicListingDetail?> GetByIdAsync(Guid listingId, Guid? viewerUserId, CancellationToken cancellationToken = default)
    {
        var row = await Visible(period: null, availableFrom: null, availableTo: null, viewerUserId)
            .Where(x => x.ListingId == listingId)
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : ToDetail(row);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublicListingSummary>>> GetCurrentAsync(
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);

        var ids = propertyIds.Where(id => id != Guid.Empty).Distinct().Take(PageParameters.MaxPageSize).ToArray();

        if (ids.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<PublicListingSummary>>();
        }

        var rows = await Visible(period: null, availableFrom: null, availableTo: null)
            .Where(x => ids.Contains(x.PropertyId))
            .OrderBy(x => x.Purpose).ThenBy(x => x.ListingId)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PublicListingSummary>)group.Select(row => ToSummary(row, searchedPeriod: null)).ToArray());
    }

    public async Task<IReadOnlyDictionary<Guid, PropertyListingReference>> GetByListingIdsAsync(
        IReadOnlyCollection<Guid> listingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listingIds);

        var ids = listingIds.Where(id => id != Guid.Empty).Distinct().Take(PageParameters.MaxPageSize).ToArray();

        if (ids.Length == 0)
        {
            return new Dictionary<Guid, PropertyListingReference>();
        }

        var rows = await Visible(period: null, availableFrom: null, availableTo: null)
            .Where(x => ids.Contains(x.ListingId))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.ListingId,
            row => new PropertyListingReference(row.PropertyId, ToSummary(row, searchedPeriod: null)));
    }

    private static PublicListingSummary ToSummary(PublicListingReadRow x, RentPeriod? searchedPeriod) =>
        new(x.ListingId, (ListingPurpose)x.Purpose, x.Title, PriceOf(x, searchedPeriod), (PropertyType)x.PropertyType,
            new PublicListingLocation(x.Country, x.Region, x.City, x.District), x.PublishedAt, OptionsOf(x),
            x.PropertyId, x.CoverImageId, x.Bedrooms, x.Bathrooms, x.AreaSqm, x.MatchPercentage, x.IsFavorite);

    private static PublicListingDetail ToDetail(PublicListingReadRow x) =>
        new(x.ListingId, (ListingPurpose)x.Purpose, x.Title, x.Description, PriceOf(x, searchedPeriod: null), (PropertyType)x.PropertyType,
            new PublicListingLocation(x.Country, x.Region, x.City, x.District), x.PublishedAt,
            OptionsOf(x), TermsOf(x), BlockedOf(x),
            x.PropertyId, x.CoverImageId, x.Bedrooms, x.Bathrooms, x.AreaSqm, x.MatchPercentage, x.IsFavorite);

    private const string LikeEscape = "\\";

    /// <summary>A contains-pattern for PostgreSQL <c>ILIKE</c> with the user's wildcards neutralized.</summary>
    private static string LikePattern(string text) =>
        $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

    // The plain listing price, or, when the search asked for a pricing period the listing offers, that option.
    private static PublicListingPrice PriceOf(PublicListingReadRow x, RentPeriod? searchedPeriod) =>
        searchedPeriod is { } period && x.PeriodAmount is { } amount && x.PeriodCurrency is { } currency
            ? new PublicListingPrice(amount, currency, period)
            : new PublicListingPrice(x.Amount, x.Currency, (RentPeriod?)x.RentPeriod);

    private static IReadOnlyList<PublicListingPrice>? OptionsOf(PublicListingReadRow x) => ParsePricingOptions(x.PricingOptionsJson);

    private static PublicRentalTerms? TermsOf(PublicListingReadRow x) => ParseRentalTerms(x.RentalTermsJson);

    /// <summary>The pricing options of a content snapshot (<c>Payload.PricingOptions</c>), shortest period first (Daily, Weekly, Monthly, Yearly); null when there are none.</summary>
    internal static IReadOnlyList<PublicListingPrice>? ParsePricingOptions(string? pricingOptionsJson)
    {
        if (string.IsNullOrWhiteSpace(pricingOptionsJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(pricingOptionsJson);

        return document.RootElement.ValueKind != JsonValueKind.Array
            ? null
            : document.RootElement.EnumerateArray()
                .Select(option => new PublicListingPrice(
                    option.GetProperty("Amount").GetDecimal(),
                    option.GetProperty("Currency").GetString()!,
                    (RentPeriod)option.GetProperty("Period").GetInt32()))
                .OrderBy(option => option.RentPeriod switch
                {
                    RentPeriod.Daily => 1,
                    RentPeriod.Weekly => 2,
                    RentPeriod.Monthly => 3,
                    _ => 4,
                })
                .ToArray();
    }

    /// <summary>The rental terms of a content snapshot (<c>Payload.RentalTerms</c>); null when there are none.</summary>
    internal static PublicRentalTerms? ParseRentalTerms(string? rentalTermsJson)
    {
        if (string.IsNullOrWhiteSpace(rentalTermsJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(rentalTermsJson);
        var terms = document.RootElement;

        if (terms.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PublicRentalTerms(
            terms.GetProperty("TimeZoneId").GetString()!,
            TimeOnly.ParseExact(terms.GetProperty("CheckInTime").GetString()!, "HH:mm:ss", CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(terms.GetProperty("CheckOutTime").GetString()!, "HH:mm:ss", CultureInfo.InvariantCulture),
            terms.GetProperty("MinimumStayDays").ValueKind == JsonValueKind.Null ? null : terms.GetProperty("MinimumStayDays").GetInt32(),
            terms.GetProperty("Windows").EnumerateArray()
                .Select(window => new PublicDateRange(
                    DateOnly.ParseExact(window.GetProperty("StartDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(window.GetProperty("EndDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .ToArray());
    }

    private static IReadOnlyList<PublicDateRange>? BlockedOf(PublicListingReadRow x)
    {
        if (string.IsNullOrWhiteSpace(x.BlockedRangesText))
        {
            return null;
        }

        return x.BlockedRangesText.Split(',')
            .Select(range => range.Split('|'))
            .Select(parts => new PublicDateRange(
                DateOnly.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .ToArray();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
