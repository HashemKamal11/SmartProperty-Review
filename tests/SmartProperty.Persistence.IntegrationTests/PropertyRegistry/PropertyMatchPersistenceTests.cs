using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves an AI match result survives a write to and a read from real PostgreSQL, and that the database protects
/// its percentage range and its links to the user and the property.
/// </summary>
public sealed class PropertyMatchPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    [Fact]
    public async Task EveryFieldRoundTrips()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        var match = new PropertyMatch(
            Guid.NewGuid(), userId, propertyId, 87.25m, "Close to the metro.", "2 bed flat in Nasr City", CreatedAt);

        await SaveAsync(match);

        await using var reading = Host.CreateVerificationContext();
        var reloaded = await reading.PropertyMatches.AsNoTracking().SingleAsync(row => row.Id == match.Id);
        Assert.Equal(userId, reloaded.UserId);
        Assert.Equal(propertyId, reloaded.PropertyId);
        Assert.Equal(87.25m, reloaded.MatchPercentage);
        Assert.Equal("Close to the metro.", reloaded.Description);
        Assert.Equal("2 bed flat in Nasr City", reloaded.Query);
        Assert.Equal(CreatedAt, reloaded.CreatedAt);
    }

    [Fact]
    public async Task TheSameUserAndPropertyCanBeMatchedMoreThanOnce()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();

        await SaveAsync(NewMatch(userId, propertyId, 60m));
        await SaveAsync(NewMatch(userId, propertyId, 75m));

        await using var reading = Host.CreateVerificationContext();
        Assert.Equal(2, await reading.PropertyMatches.CountAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    public async Task BoundaryPercentagesAreAcceptedByTheDatabase(string percentage)
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();

        await InsertRawAsync(userId, propertyId, decimal.Parse(percentage, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public async Task OutOfRangePercentagesAreRejectedByTheDatabase(string percentage)
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRawAsync(
            userId, propertyId, decimal.Parse(percentage, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_matching_property_matches_match_percentage", exception.ConstraintName);
    }

    [Fact]
    public async Task AMatchForAnUnknownUserIsRejected()
    {
        var (_, propertyId) = await SeedUserAndPropertyAsync();

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(NewMatch(Guid.NewGuid(), propertyId, 50m)));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }

    [Fact]
    public async Task AMatchForAnUnknownPropertyIsRejected()
    {
        var (userId, _) = await SeedUserAndPropertyAsync();

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(NewMatch(userId, Guid.NewGuid(), 50m)));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }

    [Fact]
    public async Task APropertyWithMatchesCannotBeDeleted()
    {
        var (userId, propertyId) = await SeedUserAndPropertyAsync();
        await SaveAsync(NewMatch(userId, propertyId, 50m));

        await using var deleting = Host.CreateVerificationContext();
        var property = await deleting.Properties.SingleAsync(row => row.Id == propertyId);
        deleting.Properties.Remove(property);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => deleting.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }

    private static PropertyMatch NewMatch(Guid userId, Guid propertyId, decimal percentage)
    {
        return new PropertyMatch(Guid.NewGuid(), userId, propertyId, percentage, "Good fit.", "a query", CreatedAt);
    }

    private async Task<(Guid UserId, Guid PropertyId)> SeedUserAndPropertyAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Buyer", CreatedAt);
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt);

        await using var seeding = Host.CreateVerificationContext();
        seeding.Users.Add(user);
        seeding.Properties.Add(property);
        await seeding.SaveChangesAsync();

        return (user.Id, property.Id);
    }

    private async Task SaveAsync(PropertyMatch match)
    {
        await using var writing = Host.CreateVerificationContext();
        writing.PropertyMatches.Add(match);
        await writing.SaveChangesAsync();
    }

    private async Task InsertRawAsync(Guid userId, Guid propertyId, decimal percentage)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO matching.property_matches
                (id, user_id, property_id, match_percentage, description, query, created_at)
            VALUES
                (@id, @user_id, @property_id, @percentage, 'text', 'query', @created_at);
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("property_id", propertyId);
        command.Parameters.AddWithValue("percentage", percentage);
        command.Parameters.AddWithValue("created_at", CreatedAt);

        await command.ExecuteNonQueryAsync();
    }
}
