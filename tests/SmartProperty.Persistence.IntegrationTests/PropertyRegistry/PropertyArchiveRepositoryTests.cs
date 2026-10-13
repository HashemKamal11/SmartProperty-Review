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
/// The command-side load and the change-tracked write path behind an archive, against a real database: the
/// aggregate is loaded tracked, archived by the domain, and committed through the unit of work. Nothing here
/// calls <c>Update</c> or <c>Attach</c>.
/// </summary>
public sealed class PropertyArchiveRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset ArchivedAt = TestClock.DefaultNow.AddHours(3);

    [Fact]
    public async Task AnArchivePersistsOnlyStatusAndUpdateInstantThroughOneSelectAndOneUpdate()
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
        Host.Commands.Reset();

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var property = (await repository.GetByIdAsync(seeded.Id))!;
            property.Archive(ArchivedAt);

            var entry = context.Entry(property);
            Assert.Equal(EntityState.Modified, entry.State);
            Assert.Equal(
                [nameof(Property.Status), nameof(Property.UpdatedAt)],
                entry.Properties
                    .Where(value => value.IsModified)
                    .Select(value => value.Metadata.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            Assert.All(entry.ComplexProperty(value => value.Address).Properties, value => Assert.False(value.IsModified));

            Assert.Equal(1, await unitOfWork.SaveChangesAsync());
            Assert.Equal(EntityState.Unchanged, entry.State);
        }

        // One SELECT by primary key, one UPDATE. The SQL itself is diagnostics only and not asserted on.
        Assert.True(Host.Commands.Count == 2, string.Join(Environment.NewLine, Host.Commands.ExecutedCommands));

        await using var verification = Host.CreateVerificationContext();
        var persisted = await verification.Properties.AsNoTracking().SingleAsync(row => row.Id == seeded.Id);

        Assert.Equal(PropertyStatus.Archived, persisted.Status);
        Assert.Equal(ArchivedAt, persisted.UpdatedAt);
        Assert.Equal(PropertyType.Land, persisted.Type);
        Assert.Equal(seeded.Address, persisted.Address);
        Assert.Equal(seeded.CreatedAt, persisted.CreatedAt);
        Assert.Equal(1, await verification.Properties.CountAsync());
    }

    private async Task<Property> SeedAsync(PropertyAddress address)
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, address, TestClock.DefaultNow);

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
