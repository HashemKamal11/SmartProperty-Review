using SmartProperty.Application.PropertyRegistry.Amenities;
using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Application.PropertyRegistry.Update;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class PropertyAmenitiesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakeAmenityRepository _amenities = new();
    private readonly FakeViewRepository _views = new();
    private readonly FakeLocationRepository _locations = new();
    private readonly FakePropertyAiSynchronizer _ai = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private Area? _place;
    private readonly Amenity _pool;
    private readonly Amenity _gym;
    private readonly Amenity _balcony;

    public PropertyAmenitiesHandlerTests()
    {
        _balcony = _amenities.Seed("Balcony", 1);
        _gym = _amenities.Seed("SharedGym", 2);
        _pool = _amenities.Seed("SwimmingPool", 3);
    }

    [Fact]
    public async Task CreateAttachesTheChosenAmenitiesAndReturnsThemInDisplayOrder()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Apartment", Place.Id, AmenityCodes: ["SwimmingPool", "Balcony"]));

        Assert.True(result.IsSuccess);
        var property = Assert.Single(_properties.Added);
        Assert.Equal(2, property.Amenities.Count);
        Assert.Equal(["Balcony", "SwimmingPool"], result.Value.Amenities!.Select(amenity => amenity.Code));
    }

    [Fact]
    public async Task CreateWithoutAmenityCodesAttachesNothingAndDoesNotAskTheRepository()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Apartment", Place.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(Assert.Single(_properties.Added).Amenities);
        Assert.Empty(result.Value.Amenities!);
        Assert.Equal(0, _amenities.GetByCodesCallCount);
    }

    [Fact]
    public async Task CreateCollapsesDuplicateAndPaddedCodes()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Apartment", Place.Id, AmenityCodes: ["Balcony", " Balcony ", "Balcony"]));

        Assert.True(result.IsSuccess);
        Assert.Single(Assert.Single(_properties.Added).Amenities);
    }

    [Fact]
    public async Task CreateRejectsAnUnknownCodeNamingItAndStoresNothing()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Apartment", Place.Id, AmenityCodes: ["Balcony", "Helipad", "Moat"]));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("Helipad", result.Error.Description);
        Assert.Contains("Moat", result.Error.Description);
        Assert.DoesNotContain("Balcony", result.Error.Description);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateRejectsABlankCode(string code)
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Apartment", Place.Id, AmenityCodes: ["Balcony", code]));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_properties.Added);
    }

    [Fact]
    public async Task CodesAreMatchedExactlyIncludingCase()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Apartment", Place.Id, AmenityCodes: ["balcony"]));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task UpdateReplacesTheAmenitySetAndCommitsOnce()
    {
        var property = SeedProperty(_pool, _gym);

        var result = await UpdateHandler().Handle(
            new UpdatePropertyCommand(property.Id, "Apartment", Place.Id, AmenityCodes: ["SharedGym", "Balcony"]));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, property.Amenities.Count);
        Assert.Contains(_gym, property.Amenities);
        Assert.Contains(_balcony, property.Amenities);
        Assert.DoesNotContain(_pool, property.Amenities);
        Assert.Equal(["Balcony", "SharedGym"], result.Value.Amenities!.Select(amenity => amenity.Code));
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateWithoutAmenityCodesClearsThem()
    {
        var property = SeedProperty(_pool);

        var result = await UpdateHandler().Handle(new UpdatePropertyCommand(property.Id, "Apartment", Place.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(property.Amenities);
        Assert.Empty(result.Value.Amenities!);
    }

    [Fact]
    public async Task UpdateWithTheSameAmenitiesCommitsNothing()
    {
        var property = SeedProperty(_pool, _gym);

        var result = await UpdateHandler().Handle(
            new UpdatePropertyCommand(property.Id, "Apartment", Place.Id, AmenityCodes: ["SwimmingPool", "SharedGym"]));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateRejectsAnUnknownCodeAndLeavesTheAmenitiesUntouched()
    {
        var property = SeedProperty(_pool);

        var result = await UpdateHandler().Handle(
            new UpdatePropertyCommand(property.Id, "Apartment", Place.Id, AmenityCodes: ["Helipad"]));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("Helipad", result.Error.Description);
        Assert.Single(property.Amenities);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ListQueryReturnsEveryAmenityInDisplayOrder()
    {
        var result = await new GetAmenitiesQueryHandler(_amenities).Handle(new GetAmenitiesQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(["Balcony", "SharedGym", "SwimmingPool"], result.Value.Select(amenity => amenity.Code));
        Assert.Equal("Balcony-ar", result.Value[0].NameAr);
        Assert.Equal("Balcony-en", result.Value[0].NameEn);
    }

    private Property SeedProperty(params Amenity[] amenities)
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG", "Cairo", region: null, district: "Helwan"),
            Now.AddDays(-5),
            amenities: amenities,
            area: Place);
        _properties.Tracked.Add(property.Id, property);

        return property;
    }

    private Area Place => _place ??= _locations.SeedArea(_locations.SeedCity(_locations.SeedCountry()));

    private CreatePropertyCommandHandler CreateHandler()
    {
        return new CreatePropertyCommandHandler(
            _properties, _amenities, _views, _locations, _ai, new FakeDateTimeProvider(Now), _unitOfWork);
    }

    private UpdatePropertyCommandHandler UpdateHandler()
    {
        return new UpdatePropertyCommandHandler(
            _properties, _amenities, _views, _locations, _ai, new FakeDateTimeProvider(Now), _unitOfWork);
    }
}
