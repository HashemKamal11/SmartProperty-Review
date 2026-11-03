using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyRegistry.Views;
using SmartProperty.Domain.Locations;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class LocationsAndViewsHandlerTests
{
    private readonly FakeAiSearchGateway _gateway = new();
    private readonly FakeLocationRepository _locations = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    // ---- views

    [Fact]
    public async Task ViewsAreListedInDisplayOrderWithBothNames()
    {
        var views = new FakeViewRepository();
        views.Seed("Sea", 7);
        views.Seed("City", 1);

        var result = await new GetViewsQueryHandler(views).Handle(new GetViewsQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(["City", "Sea"], result.Value.Select(view => view.Code));
        Assert.Equal("City-ar", result.Value[0].NameAr);
        Assert.Equal("City-en", result.Value[0].NameEn);
    }

    // ---- locations: filling from the AI service

    [Fact]
    public async Task TheFirstRequestForCountriesFetchesThemStoresThemAndReturnsOurIds()
    {
        _gateway.Places = [new AiPlace(1, "Egypt", "مصر"), new AiPlace(2, "Saudi Arabia", "السعودية")];

        var result = await CreateHandler().Handle(new GetLocationsQuery(null));

        Assert.True(result.IsSuccess);
        Assert.Equal([("country", (int?)null)], _gateway.LocationCalls);
        Assert.Equal(["Egypt", "Saudi Arabia"], result.Value.Select(place => place.NameEn));
        Assert.Equal("مصر", result.Value[0].NameAr);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);

        // The ids returned are ours, not the AI service's.
        Assert.Equal(_locations.Countries.Select(country => country.Id), result.Value.Select(place => place.Id));
        Assert.Equal([1, 2], _locations.Countries.Select(country => country.AiId));
    }

    [Fact]
    public async Task ALevelThatIsAlreadyStoredIsAnsweredWithoutCallingTheAiService()
    {
        _locations.SeedCountry(1, "Egypt", "مصر");
        _gateway.LocationsError = AiSearchErrors.Unavailable("it could not be reached");

        var result = await CreateHandler().Handle(new GetLocationsQuery("country"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Egypt", Assert.Single(result.Value).NameEn);
        Assert.Empty(_gateway.LocationCalls);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task TheSecondRequestForTheSameLevelDoesNotFetchAgain()
    {
        _gateway.Places = [new AiPlace(1, "Egypt", "مصر")];
        var handler = CreateHandler();

        await handler.Handle(new GetLocationsQuery("country"));
        await handler.Handle(new GetLocationsQuery("country"));

        Assert.Single(_gateway.LocationCalls);
        Assert.Single(_locations.AddCalls);
    }

    [Theory]
    [InlineData("city", LocationLevel.Country)]
    [InlineData("AREA", LocationLevel.City)]
    [InlineData(" Compound ", LocationLevel.Area)]
    public async Task ALowerLevelIsFetchedUnderTheAiIdOfItsParentAndStoredUnderOurId(string level, LocationLevel parentLevel)
    {
        // Only the parent and its ancestors are stored: a stored child at the requested level would be served
        // from our tables and the AI service would never be asked.
        var country = _locations.SeedCountry(aiId: 10);
        var (parentId, parentAiId) = (country.Id, 10);
        if (parentLevel >= LocationLevel.City)
        {
            var city = _locations.SeedCity(country, aiId: 20);
            (parentId, parentAiId) = (city.Id, 20);
            if (parentLevel >= LocationLevel.Area)
            {
                var area = _locations.SeedArea(city, aiId: 30);
                (parentId, parentAiId) = (area.Id, 30);
            }
        }
        _gateway.Places = [new AiPlace(99, "Somewhere", "مكان")];

        var result = await CreateHandler().Handle(new GetLocationsQuery(level, parentId));

        Assert.True(result.IsSuccess);
        var call = Assert.Single(_gateway.LocationCalls);
        Assert.Equal(level.Trim().ToLowerInvariant(), call.Level);
        Assert.Equal(parentAiId, call.ParentId);
        Assert.Equal("Somewhere", Assert.Single(result.Value).NameEn);

        var stored = Assert.Single(_locations.AddCalls);
        Assert.Equal(parentId, stored.ParentId);
    }

    [Fact]
    public async Task APlaceIsStoredUnderItsParentSoSiblingsDoNotMix()
    {
        var egypt = _locations.SeedCountry(1);
        var cairo = _locations.SeedCity(egypt, 3, "Cairo", "القاهرة");
        var giza = _locations.SeedCity(egypt, 4, "Giza", "الجيزة");
        // The AI service gives each area its own id, so each city is answered with its own places.
        _gateway.Places = [new AiPlace(30, "Helwan", "حلوان")];
        await CreateHandler().Handle(new GetLocationsQuery("area", cairo.Id));
        _gateway.Places = [new AiPlace(40, "Haram", "الهرم")];
        var inGiza = await CreateHandler().Handle(new GetLocationsQuery("area", giza.Id));

        Assert.Equal(2, _locations.Areas.Count);
        Assert.Equal(cairo.Id, _locations.Areas.Single(area => area.AiId == 30).CityId);
        Assert.Equal(giza.Id, _locations.Areas.Single(area => area.AiId == 40).CityId);
        Assert.Equal("Haram", Assert.Single(inGiza.Value).NameEn);
        Assert.Equal(2, _gateway.LocationCalls.Count);
    }

    [Fact]
    public async Task AnEmptyAnswerStoresNothingAndReturnsAnEmptyList()
    {
        var country = _locations.SeedCountry();
        _gateway.Places = [];

        var result = await CreateHandler().Handle(new GetLocationsQuery("city", country.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Empty(_locations.AddCalls);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ACompetingRequestThatFilledTheLevelFirstIsNotAnError()
    {
        _gateway.Places = [new AiPlace(1, "Egypt", "مصر")];
        _unitOfWork.ExceptionToThrow = new UniqueConstraintViolationException(
            PersistenceConstraint.LocationAiId,
            new InvalidOperationException("duplicate"));
        _unitOfWork.DiscardTrackedChangesAction = () => _locations.SeedCountry(1, "Egypt", "مصر");

        var result = await CreateHandler().Handle(new GetLocationsQuery("country"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    // ---- locations: refusing a request

    [Theory]
    [InlineData("city")]
    [InlineData("area")]
    [InlineData("compound")]
    public async Task EveryLevelBelowCountryNeedsAParent(string level)
    {
        var result = await CreateHandler().Handle(new GetLocationsQuery(level));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_gateway.LocationCalls);
    }

    [Fact]
    public async Task TheEmptyIdIsNotAParent()
    {
        var result = await CreateHandler().Handle(new GetLocationsQuery("city", Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task AParentThatIsNotStoredIsNotFoundAndTheAiServiceIsNotAsked()
    {
        var result = await CreateHandler().Handle(new GetLocationsQuery("city", Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("locations.parent_not_found", result.Error!.Code);
        Assert.Empty(_gateway.LocationCalls);
    }

    [Fact]
    public async Task AParentOfTheWrongLevelIsNotFound()
    {
        var country = _locations.SeedCountry();

        // A country id used as the parent of areas: areas hang under cities.
        var result = await CreateHandler().Handle(new GetLocationsQuery("area", country.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("locations.parent_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task AnUnknownLevelIsRejectedNamingTheValidOnes()
    {
        var result = await CreateHandler().Handle(new GetLocationsQuery("street", Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("country, city, area, compound", result.Error.Description);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    public async Task ANumberIsNotALevel(string level)
    {
        var result = await CreateHandler().Handle(new GetLocationsQuery(level, Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task AnAiOutageWhileFillingALevelIsReturnedAsIs()
    {
        _gateway.LocationsError = AiSearchErrors.Unavailable("it did not answer in time");

        var result = await CreateHandler().Handle(new GetLocationsQuery("country"));

        Assert.True(result.IsFailure);
        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
        Assert.Empty(_locations.AddCalls);
    }

    [Fact]
    public async Task ADisabledIntegrationWhileFillingALevelIsReturnedAsIs()
    {
        _gateway.LocationsError = AiSearchErrors.Disabled;

        var result = await CreateHandler().Handle(new GetLocationsQuery("country"));

        Assert.True(result.IsFailure);
        Assert.Equal(AiSearchErrors.DisabledCode, result.Error!.Code);
    }

    private GetLocationsQueryHandler CreateHandler()
    {
        return new GetLocationsQueryHandler(_locations, _gateway, _unitOfWork);
    }
}
