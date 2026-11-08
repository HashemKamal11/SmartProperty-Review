using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class CreatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 10, 4, 8, 30, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakeAmenityRepository _amenities = new();
    private readonly FakeViewRepository _views = new();
    private readonly FakeLocationRepository _locations = new();
    private readonly FakePropertyAiSynchronizer _ai = new();
    private readonly FakeDateTimeProvider _clock = new(CreatedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Area _olaya;

    public CreatePropertyCommandHandlerTests()
    {
        _olaya = _locations.SeedArea(
            _locations.SeedCity(_locations.SeedCountry(1, "Saudi Arabia", "السعودية"), 1, "Riyadh", "الرياض"),
            1,
            "Al Olaya",
            "العليا");
    }

    [Fact]
    public async Task ValidCommandCreatesAndReturnsTheCanonicalProperty()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new CreatePropertyCommand(
            "Building",
            _olaya.Id,
            " King Fahd Road ",
            " 12211 ",
            24.7136m,
            46.6753m));

        Assert.True(result.IsSuccess);
        var property = Assert.Single(_properties.Added);
        Assert.NotEqual(Guid.Empty, property.Id);
        Assert.Equal(PropertyType.Building, property.Type);
        Assert.Equal(PropertyStatus.Active, property.Status);
        Assert.Equal("SA", property.Address.CountryCode);
        Assert.Equal("Riyadh", property.Address.City);
        Assert.Null(property.Address.Region);
        Assert.Equal("Al Olaya", property.Address.District);
        Assert.Equal("King Fahd Road", property.Address.AddressLine);
        Assert.Equal("12211", property.Address.PostalCode);
        Assert.Equal(24.7136m, property.Address.Latitude);
        Assert.Equal(46.6753m, property.Address.Longitude);
        Assert.Equal(CreatedAt, property.CreatedAt);
        Assert.Equal(CreatedAt, property.UpdatedAt);
        Assert.Equal(1, _clock.UtcNowCallCount);
        Assert.Equal(1, _properties.AddCallCount);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);

        Assert.Equal(property.Id, result.Value.Id);
        Assert.Equal(property.Type, result.Value.Type);
        Assert.Equal(property.Status, result.Value.Status);
        Assert.Equal(property.Address.CountryCode, result.Value.Address.CountryCode);
        Assert.Equal(property.CreatedAt, result.Value.CreatedAt);
        Assert.Equal(property.UpdatedAt, result.Value.UpdatedAt);
    }

    [Fact]
    public async Task MinimumCommandKeepsOnlyTheAreaDerivedAddressFields()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", _olaya.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal("SA", result.Value.Address.CountryCode);
        Assert.Equal("Riyadh", result.Value.Address.City);
        Assert.Null(result.Value.Address.Region);
        Assert.Equal("Al Olaya", result.Value.Address.District);
        Assert.Null(result.Value.Address.AddressLine);
        Assert.Null(result.Value.Address.PostalCode);
        Assert.Null(result.Value.Address.Latitude);
        Assert.Null(result.Value.Address.Longitude);
    }

    [Fact]
    public async Task MissingAreaReturnsValidationWithoutWriting()
    {
        var missing = await CreateHandler().Handle(new CreatePropertyCommand("Land", null));
        var empty = await CreateHandler().Handle(new CreatePropertyCommand("Land", Guid.Empty));

        Assert.True(missing.IsFailure);
        Assert.True(empty.IsFailure);
        Assert.Equal("validation.failed", missing.Error!.Code);
        Assert.Equal(ErrorType.Validation, missing.Error.Type);
        Assert.Contains("Area is required", missing.Error.Description);
        Assert.Contains("Area is required", empty.Error!.Description);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _clock.UtcNowCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnknownAreaReturnsValidationWithoutWriting()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal("Unknown area.", result.Error.Description);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AreaWhoseCountryHasNoCodeReturnsValidationWithoutWriting()
    {
        var atlantis = _locations.SeedArea(_locations.SeedCity(_locations.SeedCountry(9, "Atlantis", "أطلنطس")));

        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", atlantis.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("'Atlantis' has no country code", result.Error.Description);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task CompoundOfAnotherAreaReturnsValidationWithoutWriting()
    {
        var compound = _locations.SeedCompound(_locations.SeedArea(_locations.SeedCity(_locations.SeedCountry()), 40, "Maadi", "المعادي"));

        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", _olaya.Id, CompoundId: compound.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("The compound does not belong to the area.", result.Error!.Description);
        Assert.Empty(_properties.Added);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("House")]
    [InlineData("1")]
    public async Task InvalidTypeReturnsValidationWithoutWriting(string? type)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(type, _olaya.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _clock.UtcNowCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData("-90", null)]
    [InlineData("90", null)]
    [InlineData(null, "-180")]
    [InlineData(null, "180")]
    public async Task BoundaryAndIndependentCoordinatesAreAccepted(string? latitude, string? longitude)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Apartment",
            _olaya.Id,
            Latitude: Parse(latitude),
            Longitude: Parse(longitude)));

        Assert.True(result.IsSuccess);
        Assert.Equal(Parse(latitude), result.Value.Address.Latitude);
        Assert.Equal(Parse(longitude), result.Value.Address.Longitude);
    }

    [Theory]
    [InlineData("-90.1", null)]
    [InlineData("90.1", null)]
    [InlineData(null, "-180.1")]
    [InlineData(null, "180.1")]
    public async Task OutOfRangeCoordinatesReturnValidation(string? latitude, string? longitude)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Land",
            _olaya.Id,
            Latitude: Parse(latitude),
            Longitude: Parse(longitude)));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    private CreatePropertyCommandHandler CreateHandler()
    {
        return new CreatePropertyCommandHandler(
            _properties,
            _amenities,
            _views,
            _locations,
            _ai,
            _clock,
            _unitOfWork);
    }

    private static decimal? Parse(string? value)
    {
        return value is null
            ? null
            : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
