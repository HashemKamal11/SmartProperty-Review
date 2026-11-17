using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>The registry list filters, proven against real PostgreSQL.</summary>
public sealed class PropertySearchFilterRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultipleTypesMatchAnyOfThem()
    {
        var villa = NewProperty(PropertyType.Villa, 1);
        var apartment = NewProperty(PropertyType.Apartment, 2);
        var land = NewProperty(PropertyType.Land, 3);
        await SeedAsync(villa, apartment, land);

        var page = await SearchAsync(new PropertyListFilter(Types: [PropertyType.Villa, PropertyType.Apartment]));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([apartment.Id, villa.Id], page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task DistrictIsCaseInsensitiveAndAreaIsExact()
    {
        var hittin = NewProperty(PropertyType.Villa, 1, district: "Hittin");
        var olaya = NewProperty(PropertyType.Villa, 2, district: "Al Olaya");
        var noDistrict = NewProperty(PropertyType.Villa, 3);
        await SeedAsync(hittin, olaya, noDistrict);

        var byDistrict = await SearchAsync(new PropertyListFilter(District: "hITTIN"));

        Assert.Equal([hittin.Id], byDistrict.Items.Select(item => item.Id));
        Assert.Equal(1, byDistrict.TotalCount);

        var byUnknownArea = await SearchAsync(new PropertyListFilter(AreaId: Guid.NewGuid()));

        Assert.Empty(byUnknownArea.Items);
        Assert.Equal(0, byUnknownArea.TotalCount);
    }

    [Fact]
    public async Task BedroomsSupportAnExactCountAndAFiveOrMoreRange()
    {
        var three = NewProperty(PropertyType.Villa, 1, bedrooms: 3);
        var four = NewProperty(PropertyType.Villa, 2, bedrooms: 4);
        var five = NewProperty(PropertyType.Villa, 3, bedrooms: 5);
        var seven = NewProperty(PropertyType.Villa, 4, bedrooms: 7);
        var unknown = NewProperty(PropertyType.Villa, 5);
        await SeedAsync(three, four, five, seven, unknown);

        var exact = await SearchAsync(new PropertyListFilter(MinBedrooms: 4, MaxBedrooms: 4));
        var fiveOrMore = await SearchAsync(new PropertyListFilter(MinBedrooms: 5));

        Assert.Equal([four.Id], exact.Items.Select(item => item.Id));
        Assert.Equal([seven.Id, five.Id], fiveOrMore.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task MatchFilterUsesTheLatestMatchOfTheGivenUserOnly()
    {
        var userId = await SeedUserAsync();
        var otherUserId = await SeedUserAsync();
        var strong = NewProperty(PropertyType.Villa, 1);
        var staleHigh = NewProperty(PropertyType.Villa, 2);
        var weak = NewProperty(PropertyType.Villa, 3);
        var othersMatch = NewProperty(PropertyType.Villa, 4);
        var unmatched = NewProperty(PropertyType.Villa, 5);
        await SeedAsync(strong, staleHigh, weak, othersMatch, unmatched);

        await SeedMatchAsync(userId, strong.Id, 90m, BaseTime);
        await SeedMatchAsync(userId, staleHigh.Id, 95m, BaseTime);
        await SeedMatchAsync(userId, staleHigh.Id, 40m, BaseTime.AddHours(1));
        await SeedMatchAsync(userId, weak.Id, 50m, BaseTime);
        await SeedMatchAsync(otherUserId, othersMatch.Id, 99m, BaseTime);

        var page = await SearchAsync(new PropertyListFilter(MinMatchPercentage: 85m, MatchUserId: userId));

        Assert.Equal(1, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(strong.Id, item.Id);
        Assert.Equal(90m, item.MatchPercentage);
    }

    [Fact]
    public async Task AMatchUserWithoutAMinimumOnlyAnnotatesTheLatestPercentage()
    {
        var userId = await SeedUserAsync();
        var matched = NewProperty(PropertyType.Villa, 1);
        var unmatched = NewProperty(PropertyType.Villa, 2);
        await SeedAsync(matched, unmatched);
        await SeedMatchAsync(userId, matched.Id, 95m, BaseTime);
        await SeedMatchAsync(userId, matched.Id, 70m, BaseTime.AddHours(1));

        var page = await SearchAsync(new PropertyListFilter(MatchUserId: userId));

        Assert.Equal(2, page.TotalCount);
        Assert.Null(page.Items.Single(item => item.Id == unmatched.Id).MatchPercentage);
        Assert.Equal(70m, page.Items.Single(item => item.Id == matched.Id).MatchPercentage);
    }

    [Fact]
    public async Task FiltersCombineWithAnd()
    {
        var match = NewProperty(PropertyType.Villa, 1, district: "Hittin", bedrooms: 4);
        var wrongType = NewProperty(PropertyType.Apartment, 2, district: "Hittin", bedrooms: 4);
        var wrongDistrict = NewProperty(PropertyType.Villa, 3, district: "Olaya", bedrooms: 4);
        var wrongBedrooms = NewProperty(PropertyType.Villa, 5, district: "Hittin", bedrooms: 2);
        await SeedAsync(match, wrongType, wrongDistrict, wrongBedrooms);

        var page = await SearchAsync(new PropertyListFilter(
            Types: [PropertyType.Villa],
            District: "Hittin",
            MinBedrooms: 4,
            MaxBedrooms: 4));

        Assert.Equal([match.Id], page.Items.Select(item => item.Id));
        Assert.Equal(1, page.TotalCount);
    }

    private async Task<PropertyPage> SearchAsync(PropertyListFilter filter)
    {
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        return await repository.SearchAsync(filter, 0, 50);
    }

    [Fact]
    public async Task TextSearchRequiresEveryTermAcrossLocationAndAddress()
    {
        var both = NewProperty(PropertyType.Villa, 1, district: "Hittin", addressLine: "12 Garden Street");
        var wrongDistrict = NewProperty(PropertyType.Villa, 2, district: "Olaya", addressLine: "12 Garden Street");
        var unrelated = NewProperty(PropertyType.Apartment, 3, district: "Hittin", addressLine: "5 Palm Road");
        await SeedAsync(both, wrongDistrict, unrelated);

        var page = await SearchAsync(new PropertyListFilter(SearchTerms: ["garden", "hittin"]));

        Assert.Equal([both.Id], page.Items.Select(item => item.Id));
        Assert.Equal(1, page.TotalCount);

        var byAddress = await SearchAsync(new PropertyListFilter(SearchTerms: ["PALM"]));

        Assert.Equal([unrelated.Id], byAddress.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task TextSearchTreatsWildcardsAsPlainText()
    {
        var percent = NewProperty(PropertyType.Villa, 1, addressLine: "100% Corniche Road");
        var other = NewProperty(PropertyType.Villa, 2, addressLine: "Plain Road");
        await SeedAsync(percent, other);

        Assert.Equal([percent.Id], (await SearchAsync(new PropertyListFilter(SearchTerms: ["100%"]))).Items.Select(i => i.Id));
        Assert.Equal([percent.Id], (await SearchAsync(new PropertyListFilter(SearchTerms: ["%"]))).Items.Select(i => i.Id));
        Assert.Empty((await SearchAsync(new PropertyListFilter(SearchTerms: ["_"]))).Items);
    }

    [Fact]
    public async Task NewestIsTheDefaultOrderWhenThereIsNoSignal()
    {
        var oldest = NewProperty(PropertyType.Villa, 1);
        var middle = NewProperty(PropertyType.Villa, 2);
        var newest = NewProperty(PropertyType.Villa, 3);
        await SeedAsync(oldest, middle, newest);

        var byNewest = await SearchAsync(new PropertyListFilter(Sort: PropertyListSortOrder.Newest));
        var relevanceWithoutSignals = await SearchAsync(new PropertyListFilter());

        Assert.Equal([newest.Id, middle.Id, oldest.Id], byNewest.Items.Select(i => i.Id));
        Assert.Equal(byNewest.Items.Select(i => i.Id), relevanceWithoutSignals.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task RelevanceRanksByTheUsersLatestMatchThenNewestWithUnmatchedLast()
    {
        var userId = await SeedUserAsync();
        var best = NewProperty(PropertyType.Villa, 1);
        var good = NewProperty(PropertyType.Villa, 2);
        var newerUnmatched = NewProperty(PropertyType.Villa, 3);
        var olderUnmatched = NewProperty(PropertyType.Villa, 0);
        await SeedAsync(best, good, newerUnmatched, olderUnmatched);
        await SeedMatchAsync(userId, best.Id, 92m, BaseTime);
        await SeedMatchAsync(userId, good.Id, 80m, BaseTime);

        var page = await SearchAsync(new PropertyListFilter(MatchUserId: userId));

        Assert.Equal([best.Id, good.Id, newerUnmatched.Id, olderUnmatched.Id], page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task RelevanceRanksALocationHitAboveAnAddressLineHit()
    {
        var address = NewProperty(PropertyType.Villa, 2, addressLine: "Close to Hittin park");
        var location = NewProperty(PropertyType.Villa, 1, district: "Hittin");
        await SeedAsync(address, location);

        var page = await SearchAsync(new PropertyListFilter(SearchTerms: ["hittin"]));

        // The address line is the newer property, so only the location rank can put the older one first.
        Assert.Equal([location.Id, address.Id], page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task DistrictMatchesTheAreasCurrentNameInEitherLanguageNotAStaleCopy()
    {
        var place = NewPlace();
        // The address keeps an old district text; the area is the truth.
        var placed = NewPlacedProperty(place.Area, 1, district: "Old Shorouk");
        var unplaced = NewProperty(PropertyType.Villa, 2, district: "Hittin");
        await SeedPlacedAsync(place, placed, unplaced);

        Assert.Equal([placed.Id], (await SearchAsync(new PropertyListFilter(District: "shorouk city"))).Items.Select(i => i.Id));
        Assert.Equal([placed.Id], (await SearchAsync(new PropertyListFilter(District: "مدينة الشروق"))).Items.Select(i => i.Id));
        Assert.Empty((await SearchAsync(new PropertyListFilter(District: "Old Shorouk"))).Items);
        // A property without an area still matches its own district text.
        Assert.Equal([unplaced.Id], (await SearchAsync(new PropertyListFilter(District: "hittin"))).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task TextSearchCoversTheCityCountryAndCompoundNamesInBothLanguages()
    {
        var place = NewPlace();
        var inCompound = NewPlacedProperty(place.Area, 1, compound: place.Compound);
        var inAreaOnly = NewPlacedProperty(place.Area, 2);
        var elsewhere = NewProperty(PropertyType.Villa, 3, district: "Hittin");
        await SeedPlacedAsync(place, inCompound, inAreaOnly, elsewhere);

        foreach (var term in new[] { "القاهرة", "cairo", "مصر", "egypt" })
        {
            Assert.Equal(
                [inAreaOnly.Id, inCompound.Id],
                (await SearchAsync(new PropertyListFilter(SearchTerms: [term]))).Items.Select(i => i.Id));
        }

        Assert.Equal([inCompound.Id], (await SearchAsync(new PropertyListFilter(SearchTerms: ["الياسمين"]))).Items.Select(i => i.Id));
        Assert.Equal([inCompound.Id], (await SearchAsync(new PropertyListFilter(SearchTerms: ["jasmine"]))).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task CityAndCompoundRestrictThePlace()
    {
        var place = NewPlace();
        var otherPlace = NewPlace();
        var inCompound = NewPlacedProperty(place.Area, 1, compound: place.Compound);
        var inCity = NewPlacedProperty(place.Area, 2);
        var otherCity = NewPlacedProperty(otherPlace.Area, 3);
        await SeedPlacedAsync(place, inCompound, inCity);
        await SeedPlacedAsync(otherPlace, otherCity);

        Assert.Equal(
            [inCity.Id, inCompound.Id],
            (await SearchAsync(new PropertyListFilter(CityId: place.City.Id))).Items.Select(i => i.Id));
        Assert.Equal([inCompound.Id], (await SearchAsync(new PropertyListFilter(CompoundId: place.Compound.Id))).Items.Select(i => i.Id));
        Assert.Empty((await SearchAsync(new PropertyListFilter(CityId: Guid.NewGuid()))).Items);
    }

    [Fact]
    public async Task BathroomsFurnishedFinishingAndAreaFilter()
    {
        var bigFurnished = NewProperty(
            PropertyType.Villa, 1, bathrooms: 3, finishing: FinishingStatus.LuxuryFinished,
            physical: new PropertyPhysicalInfo(areaSqm: 250m, furnished: true));
        var smallUnfurnished = NewProperty(
            PropertyType.Villa, 2, bathrooms: 1, finishing: FinishingStatus.SemiFinished,
            physical: new PropertyPhysicalInfo(areaSqm: 80m, furnished: false));
        var unknown = NewProperty(PropertyType.Villa, 3);
        await SeedAsync(bigFurnished, smallUnfurnished, unknown);

        Assert.Equal([bigFurnished.Id], (await SearchAsync(new PropertyListFilter(MinBathrooms: 2))).Items.Select(i => i.Id));
        Assert.Equal([smallUnfurnished.Id], (await SearchAsync(new PropertyListFilter(MaxBathrooms: 1))).Items.Select(i => i.Id));
        Assert.Equal([bigFurnished.Id], (await SearchAsync(new PropertyListFilter(Furnished: true))).Items.Select(i => i.Id));
        Assert.Equal([smallUnfurnished.Id], (await SearchAsync(new PropertyListFilter(Furnished: false))).Items.Select(i => i.Id));
        Assert.Equal(
            [smallUnfurnished.Id, bigFurnished.Id],
            (await SearchAsync(new PropertyListFilter(
                FinishingStatuses: [FinishingStatus.LuxuryFinished, FinishingStatus.SemiFinished]))).Items.Select(i => i.Id));
        Assert.Equal(
            [bigFurnished.Id],
            (await SearchAsync(new PropertyListFilter(MinAreaSqm: 100m, MaxAreaSqm: 250m))).Items.Select(i => i.Id));
        Assert.Equal([smallUnfurnished.Id], (await SearchAsync(new PropertyListFilter(MaxAreaSqm: 80m))).Items.Select(i => i.Id));
    }

    private sealed record Place(Country Country, City City, Area Area, Compound Compound);

    private static Place NewPlace()
    {
        var aiId = Random.Shared.Next(1_000, 1_000_000_000);
        var country = new Country(Guid.NewGuid(), aiId, "Egypt", "مصر");
        var city = new City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
        var area = new Area(Guid.NewGuid(), aiId, city, "Shorouk City", "مدينة الشروق");
        var compound = new Compound(Guid.NewGuid(), aiId, area, "Jasmine Gardens", "حدائق الياسمين");

        return new Place(country, city, area, compound);
    }

    private static Property NewPlacedProperty(Area area, int minutesAfterBase, string? district = null, Compound? compound = null)
    {
        return new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("EG", city: area.City.NameEn, district: district ?? area.NameEn),
            BaseTime.AddMinutes(minutesAfterBase),
            area: area,
            compound: compound);
    }

    /// <summary>Saves the place and the properties in one context, so the place is inserted once.</summary>
    private async Task SeedPlacedAsync(Place place, params Property[] properties)
    {
        await using var context = Host.CreateVerificationContext();
        context.Compounds.Add(place.Compound);
        context.Properties.AddRange(properties);
        await context.SaveChangesAsync();
    }

    private static Property NewProperty(
        PropertyType type,
        int minutesAfterBase,
        string? district = null,
        int? bedrooms = null,
        string? addressLine = null,
        int? bathrooms = null,
        FinishingStatus? finishing = null,
        PropertyPhysicalInfo? physical = null)
    {
        return new Property(
            Guid.NewGuid(),
            type,
            new PropertyAddress("SA", district: district, addressLine: addressLine),
            BaseTime.AddMinutes(minutesAfterBase),
            bedrooms: bedrooms,
            bathrooms: bathrooms,
            finishingStatus: finishing,
            physical: physical);
    }

    private async Task SeedAsync(params Property[] properties)
    {
        await using var context = Host.CreateVerificationContext();
        context.Properties.AddRange(properties);
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Buyer", BaseTime);
        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task SeedMatchAsync(Guid userId, Guid propertyId, decimal percentage, DateTimeOffset createdAt)
    {
        await using var context = Host.CreateVerificationContext();
        context.PropertyMatches.Add(new PropertyMatch(
            Guid.NewGuid(), userId, propertyId, percentage, "Good fit.", "a villa in Riyadh", createdAt));
        await context.SaveChangesAsync();
    }
}
