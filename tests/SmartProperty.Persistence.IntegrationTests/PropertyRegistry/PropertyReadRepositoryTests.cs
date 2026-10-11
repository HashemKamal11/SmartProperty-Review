using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

public sealed class PropertyReadRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetByIdProjectsEveryFieldWithoutTracking()
    {
        var property = NewProperty(
            PropertyType.Building,
            "sa",
            BaseTime,
            city: "Riyadh",
            region: "Riyadh Province",
            district: "Al Olaya",
            addressLine: "King Fahd Road",
            postalCode: "12211",
            latitude: 24.7136m,
            longitude: 46.6753m);
        await SeedAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var result = await repository.GetDetailsByIdAsync(property.Id);

        Assert.NotNull(result);
        Assert.Equal(property.Id, result.Id);
        Assert.Equal(PropertyType.Building, result.Type);
        Assert.Equal(PropertyStatus.Active, result.Status);
        Assert.Equal("SA", result.Address.CountryCode);
        Assert.Equal("Riyadh", result.Address.City);
        Assert.Equal("Riyadh Province", result.Address.Region);
        Assert.Equal("Al Olaya", result.Address.District);
        Assert.Equal("King Fahd Road", result.Address.AddressLine);
        Assert.Equal("12211", result.Address.PostalCode);
        Assert.Equal(24.7136m, result.Address.Latitude);
        Assert.Equal(46.6753m, result.Address.Longitude);
        Assert.Equal(BaseTime, result.CreatedAt);
        Assert.Equal(BaseTime, result.UpdatedAt);
        Assert.Equal(1, Host.Commands.Count);
    }

    [Fact]
    public async Task GetByIdReturnsArchivedPropertyAndUnknownIdReturnsNull()
    {
        var property = NewProperty(PropertyType.Unit, "EG", BaseTime);
        property.Archive(BaseTime.AddDays(1));
        await SeedAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var archived = await repository.GetDetailsByIdAsync(property.Id);
        var missing = await repository.GetDetailsByIdAsync(Guid.NewGuid());

        Assert.NotNull(archived);
        Assert.Equal(PropertyStatus.Archived, archived.Status);
        Assert.Equal(BaseTime.AddDays(1), archived.UpdatedAt);
        Assert.Null(missing);
    }

    [Fact]
    public async Task ListWithoutFiltersIncludesActiveAndArchivedNewestFirst()
    {
        var oldest = NewProperty(PropertyType.Land, "SA", BaseTime);
        var archived = NewProperty(PropertyType.Unit, "EG", BaseTime.AddMinutes(1));
        archived.Archive(BaseTime.AddHours(1));
        var newest = NewProperty(PropertyType.Building, "SA", BaseTime.AddMinutes(2));
        await SeedAsync(oldest, archived, newest);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var result = await repository.ListAsync(null, null, null, 0, 10);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal([newest.Id, archived.Id, oldest.Id], result.Items.Select(item => item.Id));
        Assert.Equal(PropertyStatus.Archived, result.Items[1].Status);
        Assert.Equal(2, Host.Commands.Count);
    }

    [Fact]
    public async Task ListAppliesEveryFilterAndPagesTheFilteredRows()
    {
        var firstMatch = NewProperty(PropertyType.Unit, "SA", BaseTime);
        var secondMatch = NewProperty(PropertyType.Unit, "SA", BaseTime.AddMinutes(1));
        var wrongType = NewProperty(PropertyType.Land, "SA", BaseTime.AddMinutes(2));
        var wrongCountry = NewProperty(PropertyType.Unit, "EG", BaseTime.AddMinutes(3));
        var archived = NewProperty(PropertyType.Unit, "SA", BaseTime.AddMinutes(4));
        archived.Archive(BaseTime.AddHours(1));
        await SeedAsync(firstMatch, secondMatch, wrongType, wrongCountry, archived);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var result = await repository.ListAsync(
            PropertyType.Unit,
            PropertyStatus.Active,
            "SA",
            skip: 1,
            take: 1);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(firstMatch.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task EqualCreationTimesAreOrderedByIdDescending()
    {
        var lower = NewProperty(
            PropertyType.Land,
            "SA",
            BaseTime,
            id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var higher = NewProperty(
            PropertyType.Land,
            "SA",
            BaseTime,
            id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await SeedAsync(lower, higher);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var result = await repository.ListAsync(null, null, null, 0, 10);

        Assert.Equal([higher.Id, lower.Id], result.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task ListRejectsOutOfRangePaging(int skip, int take)
    {
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListAsync(null, null, null, skip, take));
        Assert.Equal(0, Host.Commands.Count);
    }

    private async Task SeedAsync(params Property[] properties)
    {
        await using var context = Host.CreateVerificationContext();
        context.Properties.AddRange(properties);
        await context.SaveChangesAsync();
    }

    private static Property NewProperty(
        PropertyType type,
        string countryCode,
        DateTimeOffset createdAt,
        string? city = null,
        string? region = null,
        string? district = null,
        string? addressLine = null,
        string? postalCode = null,
        decimal? latitude = null,
        decimal? longitude = null,
        Guid? id = null)
    {
        return new Property(
            id ?? Guid.NewGuid(),
            type,
            new PropertyAddress(
                countryCode,
                city,
                region,
                district,
                addressLine,
                postalCode,
                latitude,
                longitude),
            createdAt);
    }
}
