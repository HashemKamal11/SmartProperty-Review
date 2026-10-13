using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// Proves against PostgreSQL what the status concurrency token guards: of two scopes that both loaded a property
/// Active, only the first archive commits, and an update never succeeds against a row an archive already changed.
/// An update does not change the status, so an archive that commits after it still succeeds.
/// </summary>
public sealed class PropertyArchiveConcurrencyTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task FirstArchiveWinsAgainstStaleArchive_AndWinnerMetadataIsPreserved()
    {
        var seeded = await SeedActivePropertyAsync();
        using var winnerScope = Host.CreateScope();
        using var loserScope = Host.CreateScope();

        var winning = await GetPropertyAsync(winnerScope, seeded.Id);
        var losing = await GetPropertyAsync(loserScope, seeded.Id);

        // Both independent contexts have observed Active before either context changes or saves it.
        Assert.Equal(PropertyStatus.Active, winning.Status);
        Assert.Equal(PropertyStatus.Active, losing.Status);

        var winningAt = TestClock.DefaultNow.AddMinutes(1);
        winning.Archive(winningAt);
        losing.Archive(TestClock.DefaultNow.AddMinutes(2));

        await SaveAsync(winnerScope);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SaveAsync(loserScope));

        Assert.Equal(PersistenceResource.Property, conflict.Resource);

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);

        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(winningAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task StaleUpdateAfterArchiveConflicts_AndTheArchivedRowIsUntouched()
    {
        var seeded = await SeedActivePropertyAsync();
        using var archivingScope = Host.CreateScope();
        using var updatingScope = Host.CreateScope();

        var archiving = await GetPropertyAsync(archivingScope, seeded.Id);
        var updating = await GetPropertyAsync(updatingScope, seeded.Id);

        Assert.Equal(PropertyStatus.Active, archiving.Status);
        Assert.Equal(PropertyStatus.Active, updating.Status);

        var archivedAt = TestClock.DefaultNow.AddMinutes(1);
        archiving.Archive(archivedAt);
        Assert.True(updating.UpdateDetails(
            PropertyType.Unit,
            new PropertyAddress("AE", "Dubai"),
            TestClock.DefaultNow.AddMinutes(2)));

        await SaveAsync(archivingScope);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => SaveAsync(updatingScope));

        Assert.Equal(PersistenceResource.Property, conflict.Resource);

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);

        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(archivedAt, persisted.UpdatedAt);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal(seeded.Address, persisted.Address);
        Assert.Equal(seeded.CreatedAt, persisted.CreatedAt);
    }

    [Fact]
    public async Task UpdateCommittedBeforeArchiveDoesNotStopIt_AndTheRowIsArchivedWithTheUpdatedValues()
    {
        var seeded = await SeedActivePropertyAsync();
        using var updatingScope = Host.CreateScope();
        using var archivingScope = Host.CreateScope();

        var updating = await GetPropertyAsync(updatingScope, seeded.Id);
        var archiving = await GetPropertyAsync(archivingScope, seeded.Id);

        Assert.Equal(PropertyStatus.Active, updating.Status);
        Assert.Equal(PropertyStatus.Active, archiving.Status);

        var updatedAt = TestClock.DefaultNow.AddMinutes(1);
        var archivedAt = TestClock.DefaultNow.AddMinutes(2);
        var updatedAddress = new PropertyAddress("AE", "Dubai", latitude: 25.2m);
        Assert.True(updating.UpdateDetails(PropertyType.Unit, updatedAddress, updatedAt));
        archiving.Archive(archivedAt);

        await SaveAsync(updatingScope);

        // The update left the status Active, so the archive's token still matches and it commits. Only the
        // status and the update instant are written, so the update's values survive.
        Assert.Equal(1, await SaveAsync(archivingScope));

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);

        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(archivedAt, persisted.UpdatedAt);
        Assert.Equal(PropertyType.Unit, persisted.Type);
        Assert.Equal(updatedAddress, persisted.Address);
        Assert.Equal(seeded.CreatedAt, persisted.CreatedAt);
    }

    private static async Task<Property> GetPropertyAsync(IServiceScope scope, Guid propertyId)
    {
        return (await scope.ServiceProvider.GetRequiredService<IPropertyRepository>().GetByIdAsync(propertyId))!;
    }

    private static Task<int> SaveAsync(IServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private async Task<Property> SeedActivePropertyAsync()
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Land,
            new PropertyAddress("SA", "Riyadh"),
            TestClock.DefaultNow);

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
