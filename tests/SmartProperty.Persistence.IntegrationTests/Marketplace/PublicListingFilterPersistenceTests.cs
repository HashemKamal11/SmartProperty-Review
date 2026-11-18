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

    [Fact]
    public async Task TheDetailPageCarriesThePropertysPublicFactsAndItsOtherListing()
    {
        var world = await SeedAsync([ListingPurpose.Sale, ListingPurpose.Rent]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await ExecuteAsync(
            "UPDATE registry.properties SET built_date = '2024-03-01', finishing_status = 'LuxuryFinished', floor = 2, " +
            $"furnished = true, latitude = 24.774265, longitude = 46.738586, address_line = '12 Secret Street' WHERE id = '{world.PropertyId}'");
        await AddPhotoAsync(world.PropertyId, second, sortOrder: 2);
        await AddPhotoAsync(world.PropertyId, first, sortOrder: 1);
        // The amenity and view catalogues are seeded by migrations only, so the test adds the two it uses.
        var amenity = $"Pool{Guid.NewGuid():N}"[..20];
        var view = $"Garden{Guid.NewGuid():N}"[..20];
        await ExecuteAsync(
            "INSERT INTO registry.amenities (id, code, name_ar, name_en, sort_order, ai_code) " +
            $"VALUES ('{Guid.NewGuid()}', '{amenity}', 'مسبح خاص', 'Private pool', 1, '{amenity}')");
        await ExecuteAsync(
            $"INSERT INTO registry.views (id, code, name_ar, name_en, sort_order) VALUES ('{Guid.NewGuid()}', '{view}', 'حديقة', 'Garden', 1)");
        await ExecuteAsync(
            $"INSERT INTO registry.property_amenities (property_id, amenity_id) SELECT '{world.PropertyId}', id FROM registry.amenities WHERE code = '{amenity}'");
        await ExecuteAsync(
            $"INSERT INTO registry.property_views (property_id, view_id) SELECT '{world.PropertyId}', id FROM registry.views WHERE code = '{view}'");

        await using var scope = Scope();
        var handler = new SmartProperty.Application.Marketplace.PublicListings.GetById.GetPublicListingByIdQueryHandler(
            scope.Get<IPublicListingReadRepository>(), scope.Get<IPropertyRepository>(), scope.Get<IPropertyListingsLookup>());

        var result = await handler.Handle(new SmartProperty.Application.Marketplace.PublicListings.GetById.GetPublicListingByIdQuery(sale.ListingId));

        Assert.True(result.IsSuccess);
        var facts = result.Value.Property!;
        Assert.Equal([first, second], facts.ImageIds);
        Assert.Equal("مسبح خاص", Assert.Single(facts.Amenities).NameAr);
        Assert.Equal(view, Assert.Single(facts.Views).Code);
        Assert.Equal(new DateOnly(2024, 3, 1), facts.BuiltDate);
        Assert.Equal(FinishingStatus.LuxuryFinished, facts.Finishing);
        Assert.Equal(2, facts.Floor);
        Assert.True(facts.Furnished);
        Assert.Equal("Maadi", facts.Place!.Area.NameEn);
        Assert.Equal("المعادي", facts.Place.Area.NameAr);
        Assert.Equal("القاهرة", facts.Place.City.NameAr);
        // Rounded to about 1 km; the exact point and the street line never reach the public model.
        Assert.Equal(new PublicMapPoint(24.77m, 46.74m), facts.ApproximateLocation);
        Assert.DoesNotContain("Secret", result.Value.ToString());
        // The property's Rent listing is offered next to the Sale one, and the page's own listing is not repeated.
        var other = Assert.Single(result.Value.OtherListings!);
        Assert.Equal(rent.ListingId, other.ListingId);
        Assert.Equal(ListingPurpose.Rent, other.Purpose);
    }

    [Fact]
    public async Task GroupedByPropertyASaleAndRentPropertyIsOneCardWithBothListingsSaleFirst()
    {
        var both = await SeedAsync([ListingPurpose.Sale, ListingPurpose.Rent]);
        var bothRent = await PublishAsync(both, ListingPurpose.Rent);
        var bothSale = await PublishAsync(both, ListingPurpose.Sale);
        var saleOnly = await SeedAsync([ListingPurpose.Sale]);
        var saleOnlySale = await PublishAsync(saleOnly, ListingPurpose.Sale);

        var page = await CardsAsync(Criteria());

        Assert.Equal(2, page.TotalCount); // properties, not listings
        var card = Assert.Single(page.Items, item => item.PropertyId == both.PropertyId);
        Assert.Equal([bothSale.ListingId, bothRent.ListingId], card.Listings.Select(listing => listing.ListingId));
        Assert.Equal([ListingPurpose.Sale, ListingPurpose.Rent], card.Listings.Select(listing => listing.Purpose));
        Assert.Equal([saleOnlySale.ListingId], page.Items.Single(item => item.PropertyId == saleOnly.PropertyId).Listings.Select(l => l.ListingId));
    }

    [Fact]
    public async Task AListingFilterKeepsOnlyTheMatchingListingsOnEachCard()
    {
        var both = await SeedAsync([ListingPurpose.Sale, ListingPurpose.Rent]);
        var bothSale = await PublishAsync(both, ListingPurpose.Sale);
        var bothRent = await PublishAsync(both, ListingPurpose.Rent);
        var saleOnly = await SeedAsync([ListingPurpose.Sale]);
        await PublishAsync(saleOnly, ListingPurpose.Sale);

        // purpose=Rent: the Sale-only property disappears, and the other card shows only its Rent listing.
        var rent = await CardsAsync(Criteria() with { Purpose = ListingPurpose.Rent });
        var rentCard = Assert.Single(rent.Items);
        Assert.Equal(both.PropertyId, rentCard.PropertyId);
        Assert.Equal([bothRent.ListingId], rentCard.Listings.Select(listing => listing.ListingId));
        Assert.Equal(1, rent.TotalCount);

        // A rent price range: the card shows the rent in the period that was compared.
        var monthly = await CardsAsync(Criteria() with
        {
            Purpose = ListingPurpose.Rent, RentPeriod = RentPeriod.Monthly, Currency = "EGP", MinPrice = 10_000m, MaxPrice = 25_000m,
        });
        var monthlyListing = Assert.Single(Assert.Single(monthly.Items).Listings);
        Assert.Equal(20_000m, monthlyListing.Price.Amount);
        Assert.Equal(RentPeriod.Monthly, monthlyListing.Price.RentPeriod);

        // A sale price range: both properties match, each card with its Sale listing only.
        var sales = await CardsAsync(Criteria() with { Purpose = ListingPurpose.Sale, Currency = "EGP", MinPrice = 4_000_000m });
        Assert.Equal(2, sales.TotalCount);
        Assert.All(sales.Items, item => Assert.Equal(ListingPurpose.Sale, Assert.Single(item.Listings).Purpose));
        Assert.Contains(sales.Items, item => item.Listings[0].ListingId == bothSale.ListingId);

        // No listing of any property fits: no card at all.
        Assert.Empty((await CardsAsync(Criteria() with { Purpose = ListingPurpose.Sale, Currency = "EGP", MinPrice = 9_000_000m })).Items);
    }

    [Fact]
    public async Task CardsSortByTheViewersMatchAndCarryTheCardFacts()
    {
        var strong = await SeedAsync([ListingPurpose.Sale, ListingPurpose.Rent]);
        await PublishAsync(strong, ListingPurpose.Sale);
        await PublishAsync(strong, ListingPurpose.Rent);
        var weak = await SeedAsync([ListingPurpose.Sale]);
        await PublishAsync(weak, ListingPurpose.Sale);
        var viewer = strong.Stranger;
        await AddMatchAsync(viewer, strong.PropertyId, 94m, DateTimeOffset.UtcNow);
        await AddMatchAsync(viewer, weak.PropertyId, 70m, DateTimeOffset.UtcNow);
        await ExecuteAsync($"UPDATE registry.properties SET bedrooms = 6, area_sqm = 540 WHERE id = '{strong.PropertyId}'");

        var page = await CardsAsync(Criteria(viewer: viewer, sort: PublicListingSortOrder.Match));

        Assert.Equal([strong.PropertyId, weak.PropertyId], page.Items.Select(item => item.PropertyId));
        var first = page.Items[0];
        Assert.Equal(94m, first.MatchPercentage);
        Assert.Equal(6, first.Bedrooms);
        Assert.Equal(540m, first.AreaSqm);
        Assert.Equal(2, first.Listings.Count);
        Assert.False(first.IsFavorite);
        Assert.Equal([strong.PropertyId], (await CardsAsync(Criteria(viewer: viewer, minMatch: 85m))).Items.Select(item => item.PropertyId));
    }

    [Fact]
    public async Task CardsArePagedByProperty()
    {
        var first = await SeedAsync([ListingPurpose.Sale, ListingPurpose.Rent]);
        await PublishAsync(first, ListingPurpose.Sale);
        await PublishAsync(first, ListingPurpose.Rent);
        var second = await SeedAsync([ListingPurpose.Sale, ListingPurpose.Rent]);
        await PublishAsync(second, ListingPurpose.Sale);
        await PublishAsync(second, ListingPurpose.Rent);

        await using var scope = Scope();
        var repository = scope.Get<IPublicListingReadRepository>();
        var pageOne = await repository.SearchByPropertyAsync(Criteria(), 0, 1);
        var pageTwo = await repository.SearchByPropertyAsync(Criteria(), 1, 1);

        Assert.Equal(2, pageOne.TotalCount);
        Assert.Equal(2, Assert.Single(pageOne.Items).Listings.Count);
        Assert.Equal(2, Assert.Single(pageTwo.Items).Listings.Count);
        Assert.NotEqual(pageOne.Items[0].PropertyId, pageTwo.Items[0].PropertyId);
    }

    // ---- helpers

    private async Task<PublicPropertyCardPage> CardsAsync(PublicListingSearchCriteria criteria)
    {
        await using var scope = Scope();

        return await scope.Get<IPublicListingReadRepository>().SearchByPropertyAsync(criteria, 0, 100);
    }

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
