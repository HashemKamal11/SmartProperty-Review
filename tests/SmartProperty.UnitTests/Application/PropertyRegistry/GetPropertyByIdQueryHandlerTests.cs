using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class GetPropertyByIdQueryHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 4, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 10, 5, 9, 45, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();

    [Fact]
    public async Task AnExistingPropertyIsReturnedWithEveryFieldMapped()
    {
        var details = SeedDetails(PropertyStatus.Active);

        var result = await CreateHandler().Handle(new GetPropertyByIdQuery(details.Id));

        Assert.True(result.IsSuccess);
        var property = result.Value;
        Assert.Equal(details.Id, property.Id);
        Assert.Equal(PropertyType.Building, property.Type);
        Assert.Equal(PropertyStatus.Active, property.Status);
        Assert.Equal("SA", property.Address.CountryCode);
        Assert.Equal("Riyadh", property.Address.City);
        Assert.Equal("Riyadh Province", property.Address.Region);
        Assert.Equal("Al Olaya", property.Address.District);
        Assert.Equal("King Fahd Road", property.Address.AddressLine);
        Assert.Equal("12211", property.Address.PostalCode);
        Assert.Equal(24.7136m, property.Address.Latitude);
        Assert.Equal(46.6753m, property.Address.Longitude);
        Assert.Equal(CreatedAt, property.CreatedAt);
        Assert.Equal(UpdatedAt, property.UpdatedAt);
        Assert.Equal(details, property);
    }

    [Fact]
    public async Task AnUnknownIdReturnsNotFound()
    {
        var id = Guid.NewGuid();

        var result = await CreateHandler().Handle(new GetPropertyByIdQuery(id));

        Assert.True(result.IsFailure);
        Assert.Same(GetPropertyByIdErrors.NotFound, result.Error);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal("The property was not found.", result.Error.Description);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal(1, _properties.GetDetailsByIdCallCount);
    }

    [Fact]
    public async Task TheEmptyIdReturnsNotFoundWithoutAskingTheRepository()
    {
        var result = await CreateHandler().Handle(new GetPropertyByIdQuery(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Same(GetPropertyByIdErrors.NotFound, result.Error);
        Assert.Equal(0, _properties.GetDetailsByIdCallCount);
        Assert.Null(_properties.LastRequestedId);
    }

    [Fact]
    public async Task TheRequestedIdAndCancellationTokenAreForwarded()
    {
        var details = SeedDetails(PropertyStatus.Active);
        using var cancellation = new CancellationTokenSource();

        await CreateHandler().Handle(new GetPropertyByIdQuery(details.Id), cancellation.Token);

        Assert.Equal(1, _properties.GetDetailsByIdCallCount);
        Assert.Equal(details.Id, _properties.LastRequestedId);
        Assert.Equal(cancellation.Token, _properties.CancellationToken);
    }

    [Fact]
    public async Task AnArchivedPropertyIsReturnedUnchanged()
    {
        var details = SeedDetails(PropertyStatus.Archived);

        var result = await CreateHandler().Handle(new GetPropertyByIdQuery(details.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyStatus.Archived, result.Value.Status);
        Assert.Equal(details, result.Value);
    }

    [Fact]
    public async Task ANullQueryThrows()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler().Handle(null!));
        Assert.Equal(0, _properties.GetDetailsByIdCallCount);
    }

    private PropertyDetails SeedDetails(PropertyStatus status)
    {
        var details = new PropertyDetails(
            Guid.NewGuid(),
            PropertyType.Building,
            status,
            new PropertyAddressDetails(
                "SA",
                "Riyadh",
                "Riyadh Province",
                "Al Olaya",
                "King Fahd Road",
                "12211",
                24.7136m,
                46.6753m),
            CreatedAt,
            UpdatedAt);

        _properties.Details[details.Id] = details;

        return details;
    }

    private GetPropertyByIdQueryHandler CreateHandler()
    {
        return new GetPropertyByIdQueryHandler(_properties);
    }
}
