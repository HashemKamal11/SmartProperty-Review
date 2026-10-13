using SmartProperty.Application.PropertyRegistry.Update;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class UpdatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 10, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakeDateTimeProvider _clock = new(UpdatedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task ValidCommandCorrectsEveryMutableFieldAndCommitsOnce()
    {
        var property = SeedProperty();
        using var cancellation = new CancellationTokenSource();

        var result = await CreateHandler().Handle(
            new UpdatePropertyCommand(
                property.Id,
                "Building",
                " ae ",
                " Dubai ",
                " Dubai Emirate ",
                " Downtown ",
                " Sheikh Zayed Road ",
                " 00000 ",
                25.1972m,
                55.2744m),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyType.Building, property.Type);
        Assert.Equal("AE", property.Address.CountryCode);
        Assert.Equal("Dubai", property.Address.City);
        Assert.Equal("Dubai Emirate", property.Address.Region);
        Assert.Equal("Downtown", property.Address.District);
        Assert.Equal("Sheikh Zayed Road", property.Address.AddressLine);
        Assert.Equal("00000", property.Address.PostalCode);
        Assert.Equal(25.1972m, property.Address.Latitude);
        Assert.Equal(55.2744m, property.Address.Longitude);
        Assert.Equal(PropertyStatus.Active, property.Status);
        Assert.Equal(CreatedAt, property.CreatedAt);
        Assert.Equal(UpdatedAt, property.UpdatedAt);

        Assert.Equal(1, _clock.UtcNowCallCount);
        Assert.Equal(1, _properties.GetByIdCallCount);
        Assert.Equal(property.Id, _properties.LastRequestedId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(cancellation.Token, _properties.CancellationToken);
        Assert.Equal(0, _properties.AddCallCount);

        var details = result.Value;
        Assert.Equal(property.Id, details.Id);
        Assert.Equal(PropertyType.Building, details.Type);
        Assert.Equal(PropertyStatus.Active, details.Status);
        Assert.Equal("AE", details.Address.CountryCode);
        Assert.Equal("Dubai", details.Address.City);
        Assert.Equal("Dubai Emirate", details.Address.Region);
        Assert.Equal("Downtown", details.Address.District);
        Assert.Equal("Sheikh Zayed Road", details.Address.AddressLine);
        Assert.Equal("00000", details.Address.PostalCode);
        Assert.Equal(25.1972m, details.Address.Latitude);
        Assert.Equal(55.2744m, details.Address.Longitude);
        Assert.Equal(CreatedAt, details.CreatedAt);
        Assert.Equal(UpdatedAt, details.UpdatedAt);
    }

    [Fact]
    public async Task OmittedOrBlankOptionalValuesClearTheStoredOnes()
    {
        var property = SeedProperty(new PropertyAddress(
            "SA",
            "Riyadh",
            "Riyadh Province",
            "Al Olaya",
            "King Fahd Road",
            "12211",
            24.7136m,
            46.6753m));

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            "Land",
            "SA",
            City: "   ",
            Region: ""));

        Assert.True(result.IsSuccess);
        Assert.Null(property.Address.City);
        Assert.Null(property.Address.Region);
        Assert.Null(property.Address.District);
        Assert.Null(property.Address.AddressLine);
        Assert.Null(property.Address.PostalCode);
        Assert.Null(property.Address.Latitude);
        Assert.Null(property.Address.Longitude);
        Assert.Equal(UpdatedAt, property.UpdatedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData("Building")]
    [InlineData("Unit")]
    public async Task PropertyTypeCanBeCorrected(string type)
    {
        var property = SeedProperty();

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, type, "SA"));

        Assert.True(result.IsSuccess);
        Assert.Equal(Enum.Parse<PropertyType>(type), property.Type);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task CountryCodeCanBeCorrectedAndIsNormalized()
    {
        var property = SeedProperty();

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, "Land", " eg "));

        Assert.True(result.IsSuccess);
        Assert.Equal("EG", property.Address.CountryCode);
        Assert.Equal("EG", result.Value.Address.CountryCode);
    }

    [Theory]
    [InlineData("-90", null)]
    [InlineData("90", null)]
    [InlineData(null, "-180")]
    [InlineData(null, "180")]
    [InlineData("-90", "180")]
    public async Task BoundaryAndIndependentCoordinatesAreAccepted(string? latitude, string? longitude)
    {
        var property = SeedProperty();

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            "Land",
            "SA",
            Latitude: Parse(latitude),
            Longitude: Parse(longitude)));

        Assert.True(result.IsSuccess);
        Assert.Equal(Parse(latitude), property.Address.Latitude);
        Assert.Equal(Parse(longitude), property.Address.Longitude);
    }

    [Theory]
    [InlineData(null, "SA", null, null)]
    [InlineData("", "SA", null, null)]
    [InlineData("House", "SA", null, null)]
    [InlineData("land", "SA", null, null)]
    [InlineData("1", "SA", null, null)]
    [InlineData("Land", null, null, null)]
    [InlineData("Land", "", null, null)]
    [InlineData("Land", "   ", null, null)]
    [InlineData("Land", "S", null, null)]
    [InlineData("Land", "SAU", null, null)]
    [InlineData("Land", "S1", null, null)]
    [InlineData("Land", "SA", "-90.1", null)]
    [InlineData("Land", "SA", "90.1", null)]
    [InlineData("Land", "SA", null, "-180.1")]
    [InlineData("Land", "SA", null, "180.1")]
    public async Task InvalidInputReturnsValidationWithoutLoadingSavingOrReadingTheClock(
        string? type,
        string? countryCode,
        string? latitude,
        string? longitude)
    {
        var property = SeedProperty();

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            type,
            countryCode,
            Latitude: Parse(latitude),
            Longitude: Parse(longitude)));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(0, _properties.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
        Assert.Equal(PropertyType.Land, property.Type);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public async Task UnknownIdReturnsNotFoundWithoutSavingOrReadingTheClock()
    {
        var result = await CreateHandler().Handle(new UpdatePropertyCommand(Guid.NewGuid(), "Land", "SA"));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal(1, _properties.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task EmptyIdReturnsNotFoundWithoutTouchingTheRepository()
    {
        var result = await CreateHandler().Handle(new UpdatePropertyCommand(Guid.Empty, "Land", "SA"));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal(0, _properties.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task ArchivedPropertyReturnsConflictWithoutMutatingSavingOrReadingTheClock()
    {
        var property = SeedProperty();
        var archivedAt = CreatedAt.AddDays(1);
        property.Archive(archivedAt);

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, "Unit", "AE", "Dubai"));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.archived", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(PropertyType.Land, property.Type);
        Assert.Equal("SA", property.Address.CountryCode);
        Assert.Null(property.Address.City);
        Assert.Equal(PropertyStatus.Archived, property.Status);
        Assert.Equal(archivedAt, property.UpdatedAt);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task NoOpUpdateSucceedsWithoutSavingAndKeepsUpdatedAt()
    {
        var property = SeedProperty(new PropertyAddress("SA", city: "Riyadh", latitude: 24.7m));
        var addressBefore = property.Address;

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            "Land",
            " sa ",
            City: " Riyadh ",
            PostalCode: "  ",
            Latitude: 24.70m));

        Assert.True(result.IsSuccess);
        Assert.Same(addressBefore, property.Address);
        Assert.Equal(CreatedAt, property.UpdatedAt);
        Assert.Equal(CreatedAt, result.Value.UpdatedAt);
        Assert.Equal("Riyadh", result.Value.Address.City);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.True(_clock.UtcNowCallCount <= 1);
    }

    [Fact]
    public async Task NoOpUpdateWithTheClockBehindSucceedsWithoutSaving()
    {
        var property = SeedProperty(new PropertyAddress("SA", city: "Riyadh"));
        _clock.UtcNow = CreatedAt.AddHours(-1);

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, "Land", " sa ", " Riyadh "));

        Assert.True(result.IsSuccess);
        Assert.Equal(CreatedAt, property.UpdatedAt);
        Assert.Equal(CreatedAt, result.Value.UpdatedAt);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RealChangeWithTheClockBehindKeepsTheCurrentUpdateInstant()
    {
        var property = SeedProperty();
        _clock.UtcNow = CreatedAt.AddHours(-1);

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, "Unit", "AE"));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyType.Unit, property.Type);
        Assert.Equal("AE", property.Address.CountryCode);
        Assert.Equal(CreatedAt, property.UpdatedAt);
        Assert.Equal(CreatedAt, result.Value.UpdatedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task RealChangeTruncatesTheClockToMicrosecondPrecision()
    {
        var property = SeedProperty();
        _clock.UtcNow = UpdatedAt.AddTicks(15);

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, "Unit", "SA"));

        Assert.True(result.IsSuccess);
        Assert.Equal(UpdatedAt.AddTicks(10), property.UpdatedAt);
        Assert.Equal(UpdatedAt.AddTicks(10), result.Value.UpdatedAt);
        Assert.Equal(1, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task RepeatingAnIdenticalUpdateIsStable()
    {
        var property = SeedProperty();
        var command = new UpdatePropertyCommand(property.Id, "Unit", "AE", "Dubai");
        var handler = CreateHandler();

        var first = await handler.Handle(command);
        _clock.UtcNow = UpdatedAt.AddHours(1);
        var second = await handler.Handle(command);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(UpdatedAt, property.UpdatedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ConcurrentArchiveCommittedFirstReturnsArchivedConflict()
    {
        var property = SeedProperty();
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.PropertyConcurrencyConflict();

        var result = await CreateHandler().Handle(new UpdatePropertyCommand(property.Id, "Unit", "AE"));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.archived", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    private Property SeedProperty(PropertyAddress? address = null)
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, address ?? new PropertyAddress("SA"), CreatedAt);
        _properties.Tracked.Add(property.Id, property);
        return property;
    }

    private UpdatePropertyCommandHandler CreateHandler()
    {
        return new UpdatePropertyCommandHandler(_properties, _clock, _unitOfWork);
    }

    private static decimal? Parse(string? value)
    {
        return value is null
            ? null
            : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
