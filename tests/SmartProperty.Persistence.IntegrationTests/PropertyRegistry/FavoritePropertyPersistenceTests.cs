using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Favorites;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves a user's favorites survive real PostgreSQL: the table and its rules, the repository that reads and writes
/// them, and that the list returns the same property read model as the property endpoints.
/// </summary>
public sealed class FavoritePropertyPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    // ---- the table

    [Fact]
    public async Task AFavoriteRoundTrips()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        var favorite = new FavoriteProperty(userId, propertyId, CreatedAt.AddMinutes(5));

        await using (var writing = Host.CreateVerificationContext())
        {
            writing.FavoriteProperties.Add(favorite);
            await writing.SaveChangesAsync();
        }

        await using var reading = Host.CreateVerificationContext();
        var reloaded = await reading.FavoriteProperties.AsNoTracking().SingleAsync();
        Assert.Equal(userId, reloaded.UserId);
        Assert.Equal(propertyId, reloaded.PropertyId);
        Assert.Equal(CreatedAt.AddMinutes(5), reloaded.CreatedAt);
    }

    [Fact]
    public async Task TheSamePropertyCannotBeFavoritedTwiceByOneUser()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        await InsertAsync(userId, propertyId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(userId, propertyId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("PK_favorite_properties", exception.ConstraintName);
    }

    [Fact]
    public async Task TwoUsersCanFavoriteTheSameProperty()
    {
        var (firstUser, propertyId) = await SeedUserAndPropertyAsync();
        var secondUser = await SeedUserAsync();

        await InsertAsync(firstUser, propertyId);
        await InsertAsync(secondUser, propertyId);

        await using var context = Host.CreateVerificationContext();
        Assert.Equal(2, await context.FavoriteProperties.CountAsync());
    }

    [Fact]
    public async Task AFavoriteNeedsARealUserAndARealProperty()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();

        var unknownUser = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(Guid.NewGuid(), propertyId));
        var unknownProperty = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(userId, Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownUser.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownProperty.SqlState);
    }

    [Fact]
    public async Task APropertyOrAUserThatIsFavoritedCannotBeDeleted()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        await InsertAsync(userId, propertyId);

        var property = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM registry.properties WHERE id = '{propertyId}';"));
        var user = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM identity.users WHERE id = '{userId}';"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, property.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, user.SqlState);
    }

    [Fact]
    public async Task ASaveThatRepeatsAFavoriteIsReportedAsTheFavoriteConstraint()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        await InsertAsync(userId, propertyId);
        using var scope = Host.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .FavoriteProperties.Add(new FavoriteProperty(userId, propertyId, CreatedAt));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => unitOfWork.SaveChangesAsync());

        Assert.Equal(PersistenceConstraint.FavoriteProperty, exception.Constraint);
    }

    // ---- the repository

    [Fact]
    public async Task FindReturnsOnlyTheUsersOwnFavorite()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        var other = await SeedUserAsync();
        await InsertAsync(userId, propertyId, CreatedAt.AddHours(1));
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var mine = await repository.FindAsync(userId, propertyId);
        var theirs = await repository.FindAsync(other, propertyId);

        Assert.Equal(CreatedAt.AddHours(1), mine!.CreatedAt);
        Assert.Null(theirs);
    }

    [Fact]
    public async Task AddThenSaveStoresTheFavoriteAndRemoveThenSaveDeletesIt()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repository.AddAsync(new FavoriteProperty(userId, propertyId, CreatedAt));
        await unitOfWork.SaveChangesAsync();
        Assert.NotNull(await repository.FindAsync(userId, propertyId));

        Assert.True(await repository.RemoveAsync(userId, propertyId));
        await unitOfWork.SaveChangesAsync();
        Assert.Null(await repository.FindAsync(userId, propertyId));
    }

    [Fact]
    public async Task RemoveReportsFalseWhenThereWasNothingToRemoveAndNeverTouchesAnotherUser()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        var other = await SeedUserAsync();
        await InsertAsync(other, propertyId);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var removed = await repository.RemoveAsync(userId, propertyId);
        await unitOfWork.SaveChangesAsync();

        Assert.False(removed);
        Assert.NotNull(await repository.FindAsync(other, propertyId));
    }

    [Fact]
    public async Task ListReturnsTheUsersOwnNewestFirstWithTheTotal()
    {
        var userId = await SeedUserAsync();
        var other = await SeedUserAsync();
        var oldest = await SeedPropertyAsync();
        var middle = await SeedPropertyAsync();
        var newest = await SeedPropertyAsync();
        await InsertAsync(userId, oldest, CreatedAt.AddDays(1));
        await InsertAsync(userId, middle, CreatedAt.AddDays(2));
        await InsertAsync(userId, newest, CreatedAt.AddDays(3));
        await InsertAsync(other, middle, CreatedAt.AddDays(9));
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var page = await repository.ListAsync(userId, 0, 10);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal([newest, middle, oldest], page.Items.Select(item => item.Property.Id));
        Assert.Equal(CreatedAt.AddDays(3), page.Items[0].FavoritedAt);
    }

    [Fact]
    public async Task ListPagesWithoutRepeatingOrSkippingAndTheTotalIsTheWholeList()
    {
        var userId = await SeedUserAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = await SeedPropertyAsync();
            ids.Add(id);
            await InsertAsync(userId, id, CreatedAt.AddDays(i));
        }

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var first = await repository.ListAsync(userId, 0, 2);
        var second = await repository.ListAsync(userId, 2, 2);
        var third = await repository.ListAsync(userId, 4, 2);
        var beyond = await repository.ListAsync(userId, 10, 2);

        Assert.All(new[] { first, second, third, beyond }, page => Assert.Equal(5, page.TotalCount));
        Assert.Equal(
            Enumerable.Reverse(ids),
            first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Property.Id));
        Assert.Empty(beyond.Items);
    }

    [Fact]
    public async Task ListBreaksTiesOnTheSameInstantStablyByPropertyIdDescending()
    {
        var userId = await SeedUserAsync();
        var a = await SeedPropertyAsync();
        var b = await SeedPropertyAsync();
        await InsertAsync(userId, a, CreatedAt);
        await InsertAsync(userId, b, CreatedAt);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var page = await repository.ListAsync(userId, 0, 10);

        Assert.Equal(new[] { a, b }.OrderByDescending(id => id), page.Items.Select(item => item.Property.Id));
    }

    [Fact]
    public async Task AnArchivedPropertyStaysInTheList()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        await InsertAsync(userId, propertyId);
        await ExecuteAsync($"UPDATE registry.properties SET status = 'Archived' WHERE id = '{propertyId}';");
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var page = await repository.ListAsync(userId, 0, 10);

        Assert.Equal(PropertyStatus.Archived, Assert.Single(page.Items).Property.Status);
    }

    [Fact]
    public async Task ListReturnsTheSamePropertyReadModelAsGetDetailsById()
    {
        var userId = await SeedUserAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG", city: "Cairo"), CreatedAt,
            bedrooms: 4,
            bathrooms: 3,
            finishingStatus: FinishingStatus.LuxuryFinished,
            physical: new PropertyPhysicalInfo(300m));
        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Properties.Add(property);
            await seeding.SaveChangesAsync();
        }

        await InsertAsync(userId, property.Id);
        using var scope = Host.CreateScope();
        var favorites = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();
        var properties = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var listed = Assert.Single((await favorites.ListAsync(userId, 0, 10)).Items).Property;
        var direct = await properties.GetDetailsByIdAsync(property.Id);

        Assert.Equal(direct!.Id, listed.Id);
        Assert.Equal(direct.Type, listed.Type);
        Assert.Equal(direct.Bedrooms, listed.Bedrooms);
        Assert.Equal(direct.FinishingStatus, listed.FinishingStatus);
        Assert.Equal(direct.Physical, listed.Physical);
        Assert.Equal(direct.Address, listed.Address);
    }

    [Fact]
    public async Task AnEmptyListIsAnEmptyPageWithAZeroTotal()
    {
        var userId = await SeedUserAsync();
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var page = await repository.ListAsync(userId, 0, 10);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    public async Task ListRefusesAnInvalidWindow(int skip, int take)
    {
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAsync(Guid.NewGuid(), skip, take));
    }

    [Fact]
    public async Task ListPropertyIdsReturnsTheUsersOwnNewestFirst()
    {
        var userId = await SeedUserAsync();
        var other = await SeedUserAsync();
        var first = await SeedPropertyAsync();
        var second = await SeedPropertyAsync();
        await InsertAsync(userId, first, CreatedAt.AddDays(1));
        await InsertAsync(userId, second, CreatedAt.AddDays(2));
        await InsertAsync(other, first, CreatedAt.AddDays(3));
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFavoritePropertyRepository>();

        var ids = await repository.ListPropertyIdsAsync(userId);

        Assert.Equal([second, first], ids);
    }

    // ---- helpers

    private async Task<(Guid UserId, Guid PropertyId)> SeedUserAndPropertyAsync()
    {
        return (await SeedUserAsync(), await SeedPropertyAsync());
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Buyer", CreatedAt);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Guid> SeedPropertyAsync()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt);

        await using var context = Host.CreateVerificationContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property.Id;
    }

    private async Task InsertAsync(Guid userId, Guid propertyId, DateTimeOffset? createdAt = null)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO registry.favorite_properties (user_id, property_id, created_at) VALUES (@user, @property, @at);",
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("property", propertyId);
        command.Parameters.AddWithValue("at", createdAt ?? CreatedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
