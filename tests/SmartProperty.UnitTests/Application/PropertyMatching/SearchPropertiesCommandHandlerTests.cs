using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.PropertyMatching.Search;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyMatching;

public sealed class SearchPropertiesCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeAiSearchGateway _gateway = new();
    private readonly FakePropertyRepository _properties = new();
    private readonly FakePropertyMatchRepository _matches = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePropertyListingsLookup _listings = new();
    private readonly Guid _userId = Guid.NewGuid();

    [Fact]
    public async Task ASearchStoresAMatchForEachOfOurPropertiesAndReturnsEveryResult()
    {
        var ours = Guid.NewGuid();
        var alsoOurs = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        _properties.ExistingIds.Add(alsoOurs);
        _gateway.SearchOutcome = Outcome(
            Item(Ours(ours), 0.91, descriptionAr: "وصف عربي", descriptionEn: "English"),
            Item(null, 0.80, descriptionEn: "someone else's", aiId: "EGY-SALE-APT-007694"),
            Item(Ours(alsoOurs), 0.5, titleEn: "Only a title"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "  3 bed flat  "));

        Assert.True(result.IsSuccess);
        Assert.Equal([("3 bed flat", 3)], _gateway.Searches);
        Assert.Equal(3, result.Value.Items.Count);
        Assert.Equal(2, _matches.Added.Count);

        var first = _matches.Added[0];
        Assert.Equal(_userId, first.UserId);
        Assert.Equal(ours, first.PropertyId);
        Assert.Equal(91m, first.MatchPercentage);
        Assert.Equal("وصف عربي", first.Description);
        Assert.Equal("3 bed flat", first.Query);
        Assert.Equal(Now, first.CreatedAt);

        Assert.Equal(alsoOurs, _matches.Added[1].PropertyId);
        Assert.Equal("Only a title", _matches.Added[1].Description);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AResultThatIsNotOursIsReturnedButNeverStored()
    {
        _gateway.SearchOutcome = Outcome(Item(null, 0.7, descriptionAr: "وصف", aiId: "EGY-SALE-APT-007694"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        var item = Assert.Single(result.Value.Items);
        Assert.Null(item.PropertyId);
        Assert.Null(item.ExternalId);
        Assert.Equal("EGY-SALE-APT-007694", item.AiPropertyId);
        Assert.Equal(70m, item.MatchPercentage);
        Assert.Equal("وصف", item.Description);
        Assert.Empty(_matches.Added);
    }

    [Fact]
    public async Task TheAiServicesOwnReferenceIsNeverTakenForOneOfOurPropertyIds()
    {
        var ours = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        // The AI reference happens to equal one of our ids, but only external_id says a listing is ours.
        _gateway.SearchOutcome = Outcome(Item(null, 0.9, descriptionAr: "وصف", aiId: ours.ToString()));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.Null(Assert.Single(result.Value.Items).PropertyId);
        Assert.Empty(_matches.Added);
    }

    [Fact]
    public async Task AResultCarriesBothTheExternalIdWeSentAndTheAiReference()
    {
        var ours = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        _gateway.SearchOutcome = Outcome(Item(Ours(ours), 0.5, descriptionAr: "وصف", aiId: "PF-123"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(ours, item.PropertyId);
        Assert.Equal(_listings.ByProperty[ours].Single().ListingId.ToString(), item.ExternalId);
        Assert.Equal(_listings.ByProperty[ours].Single(), item.Listing);
        Assert.Equal("PF-123", item.AiPropertyId);
    }

    [Fact]
    public async Task EachOfOurResultsCarriesTheFullPropertyAsGetByIdReturnsItAndOthersCarryNone()
    {
        var ours = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        var stored = new PropertyDetails(
            ours,
            PropertyType.Villa,
            PropertyStatus.Active,
            new PropertyAddressDetails("SA", "Riyadh", null, "Al Malqa", null, null, null, null),
            DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(-2),
            Bedrooms: 5);
        _properties.Details[ours] = stored;
        _gateway.SearchOutcome = Outcome(
            Item(Ours(ours), 0.96, descriptionAr: "وصف"),
            Item(null, 0.8, descriptionEn: "someone else's", aiId: "PF-9"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "villa"));

        Assert.Equal(stored, result.Value.Items[0].Property);
        Assert.Null(result.Value.Items[1].Property);
    }

    [Fact]
    public async Task AnAnonymousSearchAlsoCarriesTheFullProperty()
    {
        var ours = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        _properties.Details[ours] = new PropertyDetails(
            ours,
            PropertyType.Apartment,
            PropertyStatus.Active,
            new PropertyAddressDetails("SA", null, null, null, null, null, null, null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        _gateway.SearchOutcome = Outcome(Item(Ours(ours), 0.7, descriptionAr: "وصف"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(null, "flat"));

        Assert.Equal(ours, Assert.Single(result.Value.Items).Property!.Id);
        Assert.Empty(_matches.Added);
    }

    [Fact]
    public async Task NoResultOfOursMeansNoDetailsAreLoaded()
    {
        _gateway.SearchOutcome = Outcome(Item(null, 0.9, descriptionAr: "وصف", aiId: "PF-1"));

        await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.Equal(0, _properties.GetDetailsByIdsCallCount);
    }

    [Fact]
    public async Task AnIdThatLooksLikeOursButIsNotInTheRegistryIsNotStored()
    {
        _gateway.SearchOutcome = Outcome(Item(Guid.NewGuid().ToString(), 0.7, descriptionAr: "وصف"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.Null(Assert.Single(result.Value.Items).PropertyId);
        Assert.Empty(_matches.Added);
    }

    [Fact]
    public async Task TheSearchAnswerCarriesTheAiQueryDetails()
    {
        _gateway.SearchOutcome = new AiSearchOutcome("flat", "a flat", 52600, []);

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.Equal("flat", result.Value.Query);
        Assert.Equal("a flat", result.Value.CorrectedQuery);
        Assert.Equal(52600, result.Value.TotalAvailable);
        Assert.Empty(result.Value.Items);
    }

    [Fact]
    public async Task TopKDefaultsToThreeAndIsPassedThrough()
    {
        await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));
        await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat", 7));

        Assert.Equal([3, 7], _gateway.Searches.Select(call => call.TopK));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public async Task TopKOutsideOneToTenIsRejectedBeforeTheAiServiceIsCalled(int topK)
    {
        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat", topK));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_gateway.Searches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankQueryIsRejected(string? query)
    {
        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, query));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_gateway.Searches);
    }

    [Fact]
    public async Task AQueryOverTheAiLimitIsRejected()
    {
        var result = await CreateHandler().Handle(
            new SearchPropertiesCommand(_userId, new string('q', SearchPropertiesCommandHandler.MaxQueryLength + 1)));

        Assert.True(result.IsFailure);
        Assert.Empty(_gateway.Searches);

        var atTheLimit = await CreateHandler().Handle(
            new SearchPropertiesCommand(_userId, new string('q', SearchPropertiesCommandHandler.MaxQueryLength)));
        Assert.True(atTheLimit.IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutAUserTheSearchIsAnsweredButNothingIsStored(bool emptyId)
    {
        var ours = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        _gateway.SearchOutcome = Outcome(Item(Ours(ours), 0.9, descriptionEn: "mine"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(emptyId ? Guid.Empty : null, "flat"));

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(ours, item.PropertyId);
        Assert.Empty(_matches.Added);
        Assert.Single(_gateway.Searches);
    }

    [Fact]
    public async Task AnAiOutageIsReturnedAsFailureAndStoresNothing()
    {
        _gateway.SearchError = AiSearchErrors.Unavailable("it could not be reached");

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.True(result.IsFailure);
        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
        Assert.Empty(_matches.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ADisabledIntegrationIsReturnedAsFailure()
    {
        _gateway.SearchError = AiSearchErrors.Disabled;

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.True(result.IsFailure);
        Assert.Equal(AiSearchErrors.DisabledCode, result.Error!.Code);
    }

    [Theory]
    [InlineData(0.2164, 21.64)]
    [InlineData(1.0, 100)]
    [InlineData(0.0, 0)]
    [InlineData(1.7, 100)]
    [InlineData(-0.4, 0)]
    [InlineData(0.123456, 12.35)]
    public void TheScoreBecomesAClampedPercentageRoundedToTwoPlaces(double score, double expected)
    {
        Assert.Equal((decimal)expected, SearchPropertiesCommandHandler.ToPercentage(score));
    }

    [Fact]
    public void AMissingOrInvalidScoreIsZeroPercent()
    {
        Assert.Equal(0m, SearchPropertiesCommandHandler.ToPercentage(null));
        Assert.Equal(0m, SearchPropertiesCommandHandler.ToPercentage(double.NaN));
    }

    [Fact]
    public async Task AResultWithNoTextAtAllStillGetsAStoredDescription()
    {
        var ours = Guid.NewGuid();
        _properties.ExistingIds.Add(ours);
        _gateway.SearchOutcome = Outcome(Item(Ours(ours), 0.4));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.True(result.IsSuccess);
        Assert.Equal("(no description)", Assert.Single(_matches.Added).Description);
    }

    // The external id of one of our results is the id of a visible LISTING of the property, never the property id.
    private string Ours(Guid propertyId)
    {
        return _listings.Show(propertyId).ToString();
    }

    [Fact]
    public async Task ARecordWhoseExternalIdIsAPropertyIdOrAnInvisibleListingMapsToNothing()
    {
        var property = Guid.NewGuid();
        _properties.ExistingIds.Add(property);
        _listings.Show(property);
        _gateway.SearchOutcome = Outcome(
            Item(property.ToString(), 0.9, descriptionAr: "قديم"),
            Item(Guid.NewGuid().ToString(), 0.8, descriptionAr: "غير ظاهر"));

        var result = await CreateHandler().Handle(new SearchPropertiesCommand(_userId, "flat"));

        Assert.All(result.Value.Items, item =>
        {
            Assert.Null(item.PropertyId);
            Assert.Null(item.Listing);
        });
        Assert.Empty(_matches.Added);
    }

    private SearchPropertiesCommandHandler CreateHandler()
    {
        return new SearchPropertiesCommandHandler(
            _gateway, _properties, _matches, new FakeDateTimeProvider(Now), _unitOfWork, _listings);
    }

    private static AiSearchOutcome Outcome(params AiSearchItem[] items)
    {
        return new AiSearchOutcome("q", "q", items.Length, items);
    }

    private static AiSearchItem Item(
        string? externalId,
        double? score,
        string? descriptionAr = null,
        string? descriptionEn = null,
        string? titleAr = null,
        string? titleEn = null,
        string aiId = "PF-AI-OWN-REFERENCE")
    {
        return new AiSearchItem(
            aiId, externalId, score, "Cairo", "القاهرة", "Shorouk", "الشروق", null, null, null, null, "Apartment", "شقة",
            "Sale", "بيع", 4_320_000m, "EGP", 196m, 3, 3, "Finished", false, null, "Security | Parking",
            "Available", titleAr, titleEn, descriptionAr, descriptionEn);
    }
}
