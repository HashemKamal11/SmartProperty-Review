using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyComparison;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// The comparison list against PostgreSQL: that the database itself caps a list at three and keeps one row per
/// property, and that the read model carries the property in full with the user's latest match.
/// </summary>
public sealed class PropertyComparisonPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset T0 = TestClock.DefaultNow;

    // ---- the database's own rules

    [Fact]
    public async Task APositionOutsideOneToThreeIsRefusedByTheDatabase()
    {
        var user = await SeedUserAsync();
        var property = await SeedPropertyAsync(TransactionType.Sale);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.property_comparison_items (user_id, property_id, position, added_at) "
            + $"VALUES ('{user}', '{property}', 4, now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task TwoItemsCannotShareAPositionSoAFourthCannotFit()
    {
        var user = await SeedUserAsync();
        var first = await SeedPropertyAsync(TransactionType.Sale);
        var second = await SeedPropertyAsync(TransactionType.Sale);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>();
        await repository.AddAsync(new PropertyComparisonItem(user, first, 1, T0));
        await repository.AddAsync(new PropertyComparisonItem(user, second, 1, T0));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        Assert.Equal(PersistenceConstraint.PropertyComparisonPosition, exception.Constraint);
    }

    [Fact]
    public async Task TheSamePropertyCannotBeOnAListTwice()
    {
        var user = await SeedUserAsync();
        var property = await SeedPropertyAsync(TransactionType.Sale);
        await StoreAsync(new PropertyComparisonItem(user, property, 1, T0));

        using var scope = Host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>()
            .AddAsync(new PropertyComparisonItem(user, property, 2, T0));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        Assert.Equal(PersistenceConstraint.PropertyComparisonItem, exception.Constraint);
    }

    [Fact]
    public async Task EachUserHasTheirOwnThreePositions()
    {
        var first = await SeedUserAsync();
        var second = await SeedUserAsync();
        var property = await SeedPropertyAsync(TransactionType.Sale);

        await StoreAsync(new PropertyComparisonItem(first, property, 1, T0));
        await StoreAsync(new PropertyComparisonItem(second, property, 1, T0));

        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM registry.property_comparison_items;"));
    }

    // ---- the read model

    [Fact]
    public async Task TheListIsReadInTheOrderAddedWithEachPropertyInFull()
    {
        var user = await SeedUserAsync();
        var a = await SeedPropertyAsync(TransactionType.Sale, price: 4_850_000m);
        var b = await SeedPropertyAsync(TransactionType.Sale, price: 1_380_000m);
        await StoreAsync(new PropertyComparisonItem(user, b, 1, T0));
        await StoreAsync(new PropertyComparisonItem(user, a, 2, T0.AddMinutes(1)));

        using var scope = Host.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>().GetAsync(user);

        Assert.Equal([b, a], list.Items.Select(item => item.Property.Id));
        Assert.Equal(1_380_000m, list.Items[0].Property.Listing!.Price);
        Assert.Equal(TransactionType.Sale, list.TransactionType);
        Assert.True(list.CanCompare);
        Assert.Equal(3, list.MaxItems);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<SmartProperty.Persistence.Context.ApplicationDbContext>()
            .ChangeTracker.Entries());
    }

    [Fact]
    public async Task TheMatchIsTheUsersLatestOneAndNullWhenThereIsNone()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();
        var matched = await SeedPropertyAsync(TransactionType.Rent);
        var unmatched = await SeedPropertyAsync(TransactionType.Rent);
        await StoreAsync(new PropertyComparisonItem(user, matched, 1, T0));
        await StoreAsync(new PropertyComparisonItem(user, unmatched, 2, T0.AddMinutes(1)));
        await StoreMatchAsync(user, matched, 70m, T0.AddHours(1));
        await StoreMatchAsync(user, matched, 96m, T0.AddHours(2));
        await StoreMatchAsync(user, matched, 40m, T0.AddHours(-5));
        await StoreMatchAsync(other, unmatched, 88m, T0.AddHours(3));

        using var scope = Host.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>().GetAsync(user);

        Assert.Equal(96m, list.Items.Single(item => item.Property.Id == matched).MatchPercentage);
        // Another user's match for the property is not this user's.
        Assert.Null(list.Items.Single(item => item.Property.Id == unmatched).MatchPercentage);
    }

    [Fact]
    public async Task AnArchivedPropertyStaysOnTheListAndIsShown()
    {
        var user = await SeedUserAsync();
        var property = await SeedPropertyAsync(TransactionType.Sale, archive: true);
        await StoreAsync(new PropertyComparisonItem(user, property, 1, T0));

        using var scope = Host.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>().GetAsync(user);

        Assert.Equal(PropertyStatus.Archived, Assert.Single(list.Items).Property.Status);
    }

    [Fact]
    public async Task AUserWithNoListGetsAnEmptyOne()
    {
        var user = await SeedUserAsync();

        using var scope = Host.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>().GetAsync(user);

        Assert.Empty(list.Items);
        Assert.False(list.CanCompare);
        Assert.Null(list.TransactionType);
    }

    [Fact]
    public async Task RemovingAndClearingDeleteOnlyThatUsersRows()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();
        var first = await SeedPropertyAsync(TransactionType.Sale);
        var second = await SeedPropertyAsync(TransactionType.Sale);
        await StoreAsync(new PropertyComparisonItem(user, first, 1, T0));
        await StoreAsync(new PropertyComparisonItem(user, second, 2, T0));
        await StoreAsync(new PropertyComparisonItem(other, first, 1, T0));

        using (var scope = Host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>();
            Assert.True(await repository.RemoveAsync(user, first));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using (var scope = Host.CreateScope())
        {
            // Already gone: nothing to remove.
            Assert.False(await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>().RemoveAsync(user, first));
        }

        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM registry.property_comparison_items;"));

        using (var scope = Host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPropertyComparisonRepository>().ClearAsync(user);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM registry.property_comparison_items;"));
    }

    // ---- helpers

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "User", T0);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Guid> SeedPropertyAsync(TransactionType transactionType, decimal price = 1_000_000m, bool archive = false)
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress("SA"),
            T0.AddDays(-10),
            listing: new PropertyListingInfo(transactionType, price, 200m, titleEn: "A villa"));

        if (archive)
        {
            property.Archive(T0.AddDays(-9));
        }

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }

    private async Task StoreAsync(PropertyComparisonItem item)
    {
        await using var context = Host.CreateVerificationContext();
        context.PropertyComparisonItems.Add(item);
        await context.SaveChangesAsync();
    }

    private async Task StoreMatchAsync(Guid userId, Guid propertyId, decimal percentage, DateTimeOffset createdAt)
    {
        await using var context = Host.CreateVerificationContext();
        context.PropertyMatches.Add(new PropertyMatch(
            Guid.NewGuid(), userId, propertyId, percentage, "Matches the need", "a villa in Riyadh", createdAt));
        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var context = Host.CreateVerificationContext();
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var context = Host.CreateVerificationContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
