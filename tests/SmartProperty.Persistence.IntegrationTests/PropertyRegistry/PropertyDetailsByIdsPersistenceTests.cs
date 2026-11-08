using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>The batch read model the AI search uses to return our properties in the shape the by-id endpoint returns.</summary>
public sealed class PropertyDetailsByIdsPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset T0 = TestClock.DefaultNow;

    [Fact]
    public async Task EachFoundPropertyComesBackWithTheSameReadModelAsTheSingleRead()
    {
        var first = await SeedAsync(TransactionType.Sale, 1_000_000m);
        var second = await SeedAsync(TransactionType.Rent, 8_000m);
        await SeedAsync(TransactionType.Sale, 3_000_000m);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var batch = await repository.GetDetailsByIdsAsync([first, second, Guid.NewGuid()]);

        Assert.Equal(2, batch.Count);
        foreach (var id in new[] { first, second })
        {
            var single = (await repository.GetDetailsByIdAsync(id))!;
            var fromBatch = batch.Single(details => details.Id == id);

            Assert.Equal(single.Listing, fromBatch.Listing);
            Assert.Equal(single.Address, fromBatch.Address);
            Assert.Equal(single.Status, fromBatch.Status);
            Assert.Equal(single.Type, fromBatch.Type);
        }
    }

    [Fact]
    public async Task ArchivedPropertiesAreIncludedAndNothingAskedForMeansNothingRead()
    {
        var archived = await SeedAsync(TransactionType.Sale, 1m, archive: true);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var batch = await repository.GetDetailsByIdsAsync([archived]);
        var none = await repository.GetDetailsByIdsAsync([]);

        Assert.Equal(PropertyStatus.Archived, Assert.Single(batch).Status);
        Assert.Empty(none);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<SmartProperty.Persistence.Context.ApplicationDbContext>()
            .ChangeTracker.Entries());
    }

    private async Task<Guid> SeedAsync(TransactionType transactionType, decimal price, bool archive = false)
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("SA"),
            T0.AddDays(-5),
            listing: new PropertyListingInfo(transactionType, price, 200m, titleEn: "A villa"));

        if (archive)
        {
            property.Archive(T0.AddDays(-4));
        }

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }
}
