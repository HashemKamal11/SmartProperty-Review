using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// The listing-screen filters against real PostgreSQL: several types, free text and districts in either language,
/// place ids, bedrooms, the card facts read live from the property, and the signed-in viewer's match and favourite.
/// </summary>
public sealed class PublicListingFilterPersistenceTests(PostgreSqlFixture fixture) : MarketplaceWorkflowTestBase(fixture)
{
    private static PublicListingSearchCriteria Criteria(
        IReadOnlyList<PropertyType>? types = null,
        IReadOnlyList<string>? terms = null,
        string? district = null,
        Guid? areaId = null,
        Guid? cityId = null,
        int? minBedrooms = null,
        int? maxBedrooms = null,
        decimal? minMatch = null,
        Guid? viewer = null,
        PublicListingSortOrder sort = PublicListingSortOrder.Newest) =>
        new(null, null, null, null, null, district, null, null, null, null, sort,
            PropertyTypes: types, SearchTerms: terms, AreaId: areaId, CityId: cityId,
            MinBedrooms: minBedrooms, MaxBedrooms: maxBedrooms, MinMatchPercentage: minMatch, ViewerUserId: viewer);

    [Fact]
    public async Task SeveralTypesMatchAnyOfThem()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);

        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(types: [PropertyType.Land, PropertyType.Villa])));
        Assert.Empty(await IdsAsync(Criteria(types: [PropertyType.Land, PropertyType.Apartment])));
    }

    [Fact]
    public async Task TextAndDistrictWorkInEnglishAndArabic()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);

        foreach (var term in new[] { "القاهرة", "المعادي", "maadi", "cairo", "villa" })
        {
            Assert.Equal([sale.ListingId], await IdsAsync(Criteria(terms: [term])));
        }

        Assert.Empty(await IdsAsync(Criteria(terms: ["villa", "Alexandria"])));
        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(district: "المعادي")));
        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(district: "maadi")));
        Assert.Empty(await IdsAsync(Criteria(district: "Zamalek")));
    }

    [Fact]
    public async Task AreaAndCityRestrictThePlace()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var areaId = await GuidAsync($"SELECT area_id FROM registry.properties WHERE id = '{world.PropertyId}'");
        var cityId = await GuidAsync($"SELECT city_id FROM locations.areas WHERE id = '{areaId}'");

        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(areaId: areaId)));
        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(cityId: cityId)));
        Assert.Empty(await IdsAsync(Criteria(areaId: Guid.NewGuid())));
        Assert.Empty(await IdsAsync(Criteria(cityId: Guid.NewGuid())));
    }

    [Fact]
    public async Task TheCardShowsTheLivePropertyFactsAndBedroomsFilter()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var firstPhoto = Guid.NewGuid();
        await ExecuteAsync($"UPDATE registry.properties SET bedrooms = 4, bathrooms = 3, area_sqm = 310 WHERE id = '{world.PropertyId}'");
        await AddPhotoAsync(world.PropertyId, Guid.NewGuid(), sortOrder: 2);
        await AddPhotoAsync(world.PropertyId, firstPhoto, sortOrder: 1);

        var item = Assert.Single((await SearchAsync(Criteria())).Items);

        Assert.Equal(world.PropertyId, item.PropertyId);
        Assert.Equal(firstPhoto, item.CoverImageId);
        Assert.Equal(4, item.Bedrooms);
        Assert.Equal(3, item.Bathrooms);
        Assert.Equal(310m, item.AreaSqm);
        Assert.Null(item.MatchPercentage);
        Assert.Null(item.IsFavorite);

        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(minBedrooms: 4)));
        Assert.Equal([sale.ListingId], await IdsAsync(Criteria(minBedrooms: 4, maxBedrooms: 4)));
        Assert.Empty(await IdsAsync(Criteria(minBedrooms: 5)));
        Assert.Empty(await IdsAsync(Criteria(maxBedrooms: 3)));
    }

    [Fact]
    public async Task TheViewerSeesTheirLatestMatchAndFavouriteAndCanFilterAndSortByMatch()
    {
        var strong = await SeedAsync([ListingPurpose.Sale]);
        var strongSale = await PublishAsync(strong, ListingPurpose.Sale);
        var weak = await SeedAsync([ListingPurpose.Sale]);
        var weakSale = await PublishAsync(weak, ListingPurpose.Sale);
        var unmatched = await SeedAsync([ListingPurpose.Sale]);
        var unmatchedSale = await PublishAsync(unmatched, ListingPurpose.Sale);
        var viewer = strong.Stranger;
        var now = DateTimeOffset.UtcNow;
        await AddMatchAsync(viewer, strong.PropertyId, 40m, now.AddHours(-2));
        await AddMatchAsync(viewer, strong.PropertyId, 92m, now.AddHours(-1)); // the latest one counts
        await AddMatchAsync(viewer, weak.PropertyId, 60m, now.AddHours(-1));
        await AddMatchAsync(weak.Stranger, unmatched.PropertyId, 99m, now); // someone else's match
        await ExecuteAsync(
            $"INSERT INTO registry.favorite_properties (user_id, property_id, created_at) VALUES ('{viewer}', '{weak.PropertyId}', now())");

        var page = await SearchAsync(Criteria(viewer: viewer, sort: PublicListingSortOrder.Match));

        Assert.Equal([strongSale.ListingId, weakSale.ListingId, unmatchedSale.ListingId], page.Items.Select(item => item.ListingId));
        Assert.Equal([92m, 60m, null], page.Items.Select(item => item.MatchPercentage));
        Assert.Equal([false, true, false], page.Items.Select(item => item.IsFavorite));

        Assert.Equal(
            [strongSale.ListingId],
            await IdsAsync(Criteria(viewer: viewer, minMatch: 85m)));

        await using var scope = Scope();
        var detail = await scope.Get<IPublicListingReadRepository>().GetByIdAsync(weakSale.ListingId, viewer);
        Assert.Equal(60m, detail!.MatchPercentage);
        Assert.True(detail.IsFavorite);
        var anonymous = await scope.Get<IPublicListingReadRepository>().GetByIdAsync(weakSale.ListingId);
        Assert.Null(anonymous!.MatchPercentage);
        Assert.Null(anonymous.IsFavorite);
    }

    [Fact]
    public async Task MatchFilteringOrSortingWithoutAViewerIsRefused()
    {
        await using var scope = Scope();
        var repository = scope.Get<IPublicListingReadRepository>();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchAsync(Criteria(minMatch: 50m), 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchAsync(Criteria(sort: PublicListingSortOrder.Match), 0, 10));
    }

    // ---- helpers

    private async Task<ListingDraftResult> PublishAsync(World world, ListingPurpose purpose)
    {
        var draft = await SubmittedAsync(world, purpose);
        var approved = await ApproveAsync(world, draft.SubmissionId);
        Assert.True(approved.IsSuccess, approved.Error?.Code);

        return draft;
    }

    private async Task<PublicListingPage> SearchAsync(PublicListingSearchCriteria criteria)
    {
        await using var scope = Scope();

        return await scope.Get<IPublicListingReadRepository>().SearchAsync(criteria, 0, 100);
    }

    private async Task<Guid[]> IdsAsync(PublicListingSearchCriteria criteria) =>
        (await SearchAsync(criteria)).Items.Select(item => item.ListingId).ToArray();

    private Task AddPhotoAsync(Guid propertyId, Guid imageId, int sortOrder) =>
        ExecuteAsync(
            "INSERT INTO registry.property_images (id, property_id, storage_key, file_name, content_type, size_bytes, sort_order, created_at) " +
            $"VALUES ('{imageId}', '{propertyId}', 'k/{imageId}', 'p.jpg', 'image/jpeg', 10, {sortOrder}, now())");

    private Task AddMatchAsync(Guid userId, Guid propertyId, decimal percentage, DateTimeOffset at) =>
        ExecuteAsync(
            "INSERT INTO matching.property_matches (id, user_id, property_id, match_percentage, description, query, created_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{userId}', '{propertyId}', {percentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 'fit', 'q', '{at:O}')");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
