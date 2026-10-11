using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// The command-side load and the change-tracked write path behind a property correction, against a real database:
/// the aggregate is loaded tracked, mutated by the domain, and committed through the unit of work. Nothing here
/// calls <c>Update</c> or <c>Attach</c>.
/// </summary>
public sealed class PropertyUpdateRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset UpdatedAt = TestClock.DefaultNow.AddHours(3);

    [Fact]
    public async Task GetByIdReturnsATrackedUnchangedAggregate()
    {
        var seeded = await SeedAsync();

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var property = await repository.GetByIdAsync(seeded.Id);

        Assert.NotNull(property);
        Assert.Equal(seeded.Id, property.Id);
        Assert.Equal(EntityState.Unchanged, context.Entry(property).State);
        Assert.Single(context.ChangeTracker.Entries<Property>());
    }

    [Fact]
    public async Task GetByIdReturnsNullForAMissingId()
    {
        await SeedAsync();

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        Assert.Null(await repository.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ARealUpdatePersistsEveryMutableFieldThroughOneSelectAndOneUpdate()
    {
        var seeded = await SeedAsync(new PropertyAddress("SA", "Riyadh", latitude: 24.7136m));
        Host.Commands.Reset();

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var property = (await repository.GetByIdAsync(seeded.Id))!;
            var changed = property.UpdateDetails(
                PropertyType.Building,
                new PropertyAddress(
                    " ae ",
                    " Dubai ",
                    " Dubai Emirate ",
                    " Downtown ",
                    " Sheikh Zayed Road ",
                    " 00000 ",
                    25.1972m,
                    55.2744m),
                UpdatedAt);

            Assert.True(changed);
            Assert.Equal(EntityState.Modified, context.Entry(property).State);
            Assert.Equal(1, await unitOfWork.SaveChangesAsync());
            Assert.Equal(EntityState.Unchanged, context.Entry(property).State);
        }

        // One SELECT by primary key, one UPDATE. The SQL itself is diagnostics only and not asserted on.
        Assert.True(Host.Commands.Count == 2, string.Join(Environment.NewLine, Host.Commands.ExecutedCommands));

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);

        Assert.Equal(PropertyType.Building, persisted.Type);
        Assert.Equal("AE", persisted.Address.CountryCode);
        Assert.Equal("Dubai", persisted.Address.City);
        Assert.Equal("Dubai Emirate", persisted.Address.Region);
        Assert.Equal("Downtown", persisted.Address.District);
        Assert.Equal("Sheikh Zayed Road", persisted.Address.AddressLine);
        Assert.Equal("00000", persisted.Address.PostalCode);
        Assert.Equal(25.1972m, persisted.Address.Latitude);
        Assert.Equal(55.2744m, persisted.Address.Longitude);
        Assert.Equal(PropertyStatus.Active, persisted.Status);
        Assert.Equal(seeded.CreatedAt, persisted.CreatedAt);
        Assert.Equal(UpdatedAt, persisted.UpdatedAt);
        Assert.Equal(1, await verification.Properties.CountAsync());
    }

    [Fact]
    public async Task NullOptionalValuesClearThePersistedOnes()
    {
        var seeded = await SeedAsync(new PropertyAddress(
            "SA",
            "Riyadh",
            "Riyadh Province",
            "Al Olaya",
            "King Fahd Road",
            "12211",
            24.7136m,
            46.6753m));

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var property = (await repository.GetByIdAsync(seeded.Id))!;
            Assert.True(property.UpdateDetails(PropertyType.Land, new PropertyAddress("SA"), UpdatedAt));
            await unitOfWork.SaveChangesAsync();
        }

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);

        Assert.Equal("SA", persisted.Address.CountryCode);
        Assert.Null(persisted.Address.City);
        Assert.Null(persisted.Address.Region);
        Assert.Null(persisted.Address.District);
        Assert.Null(persisted.Address.AddressLine);
        Assert.Null(persisted.Address.PostalCode);
        Assert.Null(persisted.Address.Latitude);
        Assert.Null(persisted.Address.Longitude);
        Assert.Equal(seeded.Id, persisted.Id);
        Assert.Equal(seeded.CreatedAt, persisted.CreatedAt);
        Assert.Equal(UpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task ANoOpUpdateLeavesTheAggregateUnchangedAndWritesNothing()
    {
        var seeded = await SeedAsync(new PropertyAddress("SA", "Riyadh", latitude: 24.7m));
        Host.Commands.Reset();

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var property = (await repository.GetByIdAsync(seeded.Id))!;
            var changed = property.UpdateDetails(
                PropertyType.Land,
                new PropertyAddress(" sa ", " Riyadh ", latitude: 24.70m),
                UpdatedAt);

            Assert.False(changed);
            Assert.Equal(EntityState.Unchanged, context.Entry(property).State);
            Assert.False(context.ChangeTracker.HasChanges());

            // The handler does not commit a no-op; even if something did, there would be nothing to write.
            Assert.Equal(0, await unitOfWork.SaveChangesAsync());
        }

        // Only the SELECT: no UPDATE was issued.
        Assert.True(Host.Commands.Count == 1, string.Join(Environment.NewLine, Host.Commands.ExecutedCommands));

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);
        Assert.Equal(seeded.UpdatedAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task AnArchivedPropertyIsLoadedButCannotBeCorrected()
    {
        var seeded = await SeedAsync(archive: true);

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var property = (await repository.GetByIdAsync(seeded.Id))!;

            Assert.Equal(PropertyStatus.Archived, property.Status);
            Assert.Throws<InvalidOperationException>(() =>
                property.UpdateDetails(PropertyType.Unit, new PropertyAddress("AE"), UpdatedAt));
            Assert.False(context.ChangeTracker.HasChanges());
        }

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);
        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal("SA", persisted.Address.CountryCode);
        Assert.Equal(seeded.UpdatedAt, persisted.UpdatedAt);
    }

    private async Task<Property> SeedAsync(PropertyAddress? address = null, bool archive = false)
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Land,
            address ?? new PropertyAddress("SA"),
            TestClock.DefaultNow);

        if (archive)
        {
            property.Archive(TestClock.DefaultNow.AddHours(1));
        }

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
