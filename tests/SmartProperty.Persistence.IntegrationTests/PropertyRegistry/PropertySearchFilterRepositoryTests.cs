using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Domain.Identity;
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
    public async Task PriceRangeIsInclusiveAndExcludesUnpricedProperties()
    {
        var cheap = NewProperty(PropertyType.Villa, 1, price: 1000m);
        var low = NewProperty(PropertyType.Villa, 2, price: 2000m);
        var high = NewProperty(PropertyType.Villa, 3, price: 3000m);
        var expensive = NewProperty(PropertyType.Villa, 4, price: 9000m);
        var unpriced = NewProperty(PropertyType.Villa, 5);
        await SeedAsync(cheap, low, high, expensive, unpriced);

        var page = await SearchAsync(new PropertyListFilter(MinPrice: 2000m, MaxPrice: 3000m));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([high.Id, low.Id], page.Items.Select(item => item.Id));
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
        var match = NewProperty(PropertyType.Villa, 1, district: "Hittin", price: 2500m, bedrooms: 4);
        var wrongType = NewProperty(PropertyType.Apartment, 2, district: "Hittin", price: 2500m, bedrooms: 4);
        var wrongDistrict = NewProperty(PropertyType.Villa, 3, district: "Olaya", price: 2500m, bedrooms: 4);
        var wrongPrice = NewProperty(PropertyType.Villa, 4, district: "Hittin", price: 9000m, bedrooms: 4);
        var wrongBedrooms = NewProperty(PropertyType.Villa, 5, district: "Hittin", price: 2500m, bedrooms: 2);
        await SeedAsync(match, wrongType, wrongDistrict, wrongPrice, wrongBedrooms);

        var page = await SearchAsync(new PropertyListFilter(
            Types: [PropertyType.Villa],
            District: "Hittin",
            MinPrice: 2000m,
            MaxPrice: 3000m,
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
    public async Task TextSearchRequiresEveryTermAcrossTitleDescriptionAndLocation()
    {
        var titled = NewProperty(PropertyType.Villa, 1, district: "Hittin", title: "Luxury villa");
        var described = NewProperty(PropertyType.Villa, 2, district: "Olaya", description: "A quiet villa near the park");
        var wrongDistrict = NewProperty(PropertyType.Villa, 3, district: "Olaya", title: "Luxury villa");
        var unrelated = NewProperty(PropertyType.Apartment, 4, district: "Hittin", title: "Small flat");
        await SeedAsync(titled, described, wrongDistrict, unrelated);

        var page = await SearchAsync(new PropertyListFilter(SearchTerms: ["villa", "hittin"]));

        Assert.Equal([titled.Id], page.Items.Select(item => item.Id));
        Assert.Equal(1, page.TotalCount);

        var byDescription = await SearchAsync(new PropertyListFilter(SearchTerms: ["QUIET"]));

        Assert.Equal([described.Id], byDescription.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task TextSearchTreatsWildcardsAsPlainText()
    {
        var percent = NewProperty(PropertyType.Villa, 1, title: "100% finished villa");
        var other = NewProperty(PropertyType.Villa, 2, title: "Plain villa");
        await SeedAsync(percent, other);

        Assert.Equal([percent.Id], (await SearchAsync(new PropertyListFilter(SearchTerms: ["100%"]))).Items.Select(i => i.Id));
        Assert.Equal([percent.Id], (await SearchAsync(new PropertyListFilter(SearchTerms: ["%"]))).Items.Select(i => i.Id));
        Assert.Empty((await SearchAsync(new PropertyListFilter(SearchTerms: ["_"]))).Items);
    }

    [Fact]
    public async Task ListingStatusFiltersAvailableSoldAndRented()
    {
        var available = NewProperty(PropertyType.Villa, 1, listingStatus: ListingStatus.Available);
        var sold = NewProperty(PropertyType.Villa, 2, listingStatus: ListingStatus.Sold);
        var rented = NewProperty(PropertyType.Villa, 3, listingStatus: ListingStatus.Rented);
        await SeedAsync(available, sold, rented);

        Assert.Equal([sold.Id], (await SearchAsync(new PropertyListFilter(ListingStatus: ListingStatus.Sold))).Items.Select(i => i.Id));
        Assert.Equal([available.Id], (await SearchAsync(new PropertyListFilter(ListingStatus: ListingStatus.Available))).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task PriceSortsPutUnpricedPropertiesLastAndNewestIsTheDefaultFallback()
    {
        var cheap = NewProperty(PropertyType.Villa, 1, price: 1000m);
        var mid = NewProperty(PropertyType.Villa, 2, price: 2000m);
        var dear = NewProperty(PropertyType.Villa, 3, price: 3000m);
        var unpriced = NewProperty(PropertyType.Villa, 4);
        await SeedAsync(cheap, mid, dear, unpriced);

        var asc = await SearchAsync(new PropertyListFilter(Sort: PropertyListSortOrder.PriceAsc));
        var desc = await SearchAsync(new PropertyListFilter(Sort: PropertyListSortOrder.PriceDesc));
        var newest = await SearchAsync(new PropertyListFilter(Sort: PropertyListSortOrder.Newest));
        var relevanceWithoutSignals = await SearchAsync(new PropertyListFilter());

        Assert.Equal([cheap.Id, mid.Id, dear.Id, unpriced.Id], asc.Items.Select(i => i.Id));
        Assert.Equal([dear.Id, mid.Id, cheap.Id, unpriced.Id], desc.Items.Select(i => i.Id));
        Assert.Equal([unpriced.Id, dear.Id, mid.Id, cheap.Id], newest.Items.Select(i => i.Id));
        Assert.Equal(newest.Items.Select(i => i.Id), relevanceWithoutSignals.Items.Select(i => i.Id));
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
    public async Task RelevanceRanksATitleHitAboveALocationHitAboveADescriptionHit()
    {
        var description = NewProperty(PropertyType.Villa, 3, description: "Close to Hittin park");
        var location = NewProperty(PropertyType.Villa, 2, district: "Hittin");
        var title = NewProperty(PropertyType.Villa, 1, title: "Hittin villa");
        await SeedAsync(description, location, title);

        var page = await SearchAsync(new PropertyListFilter(SearchTerms: ["hittin"]));

        Assert.Equal([title.Id, location.Id, description.Id], page.Items.Select(i => i.Id));
    }

    private static Property NewProperty(
        PropertyType type,
        int minutesAfterBase,
        string? district = null,
        decimal? price = null,
        int? bedrooms = null,
        string? title = null,
        string? description = null,
        ListingStatus? listingStatus = null)
    {
        var hasListing = price is not null || title is not null || description is not null || listingStatus is not null;

        return new Property(
            Guid.NewGuid(),
            type,
            new PropertyAddress("SA", district: district),
            BaseTime.AddMinutes(minutesAfterBase),
            bedrooms: bedrooms,
            listing: hasListing
                ? new PropertyListingInfo(
                    price: price,
                    listingStatus: listingStatus ?? ListingStatus.Available,
                    titleEn: title,
                    descriptionEn: description)
                : null);
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
