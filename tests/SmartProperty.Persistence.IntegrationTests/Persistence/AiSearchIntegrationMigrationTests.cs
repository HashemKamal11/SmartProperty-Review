using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Proves the AI search integration migration converts the data that already exists, in the right order, and that
/// it can be rolled back. Each test builds the schema as it was before the migration, writes rows the old way with
/// plain SQL, migrates forward, and reads the rows back with plain SQL.
/// </summary>
public sealed class AiSearchIntegrationMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261101121548_AddPropertyAmenities";
    private const string AiSearchMigration = "20261102120000_AddAiSearchIntegration";

    private static readonly DateTimeOffset At = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    // ---- forward: existing rows are converted

    [Fact]
    public async Task ExistingPropertiesAreConvertedToTheAiVocabulary()
    {
        await MigrateToAsync(Previous);
        var land = await InsertOldPropertyAsync("Land", "Unfinished");
        var building = await InsertOldPropertyAsync("Building", "FullyFinished");
        var luxury = await InsertOldPropertyAsync("Unit", "SuperLux");
        var furnished = await InsertOldPropertyAsync("Unit", "Furnished");
        var semi = await InsertOldPropertyAsync("Unit", "SemiFinished");
        var unstated = await InsertOldPropertyAsync("Unit", null);

        await MigrateToLatestAsync();

        Assert.Equal(("Land", "CoreAndShell", null), await ReadAsync(land));
        Assert.Equal(("Building", "Finished", null), await ReadAsync(building));
        Assert.Equal(("Apartment", "LuxuryFinished", null), await ReadAsync(luxury));
        Assert.Equal(("Apartment", "Finished", true), await ReadAsync(furnished));
        Assert.Equal(("Apartment", "SemiFinished", null), await ReadAsync(semi));
        Assert.Equal(("Apartment", null, null), await ReadAsync(unstated));
    }

    [Fact]
    public async Task ExistingPropertiesWithNoMarketFactsHaveNothingToArchiveAndNoPlace()
    {
        await MigrateToAsync(Previous);
        var id = await InsertOldPropertyAsync("Unit", "FullyFinished");

        await MigrateToLatestAsync();

        // The commercial columns the AI migration added have since moved to listings. A property that stated nothing
        // commercial has nothing to archive, and its physical facts are still unstated.
        var row = await QuerySingleAsync(
            $"""
            SELECT area_sqm IS NULL, area_id IS NULL,
                   (SELECT count(*) FROM registry.legacy_property_commercial WHERE property_id = p.id)
            FROM registry.properties p WHERE id = '{id}';
            """);

        Assert.Equal([true, true, 0L], row);
    }

    [Fact]
    public async Task ConversionNeverTouchesTheUpdatedInstant()
    {
        await MigrateToAsync(Previous);
        var id = await InsertOldPropertyAsync("Unit", "Furnished");

        await MigrateToLatestAsync();

        var row = await QuerySingleAsync($"SELECT updated_at = created_at FROM registry.properties WHERE id = '{id}';");
        Assert.Equal([true], row);
    }

    [Fact]
    public async Task ExistingAmenityLinksSurviveAndTheAmenityGainsItsAiCode()
    {
        await MigrateToAsync(Previous);
        var id = await InsertOldPropertyAsync("Unit", null);
        await ExecuteAsync(
            $"""
            INSERT INTO registry.property_amenities (property_id, amenity_id)
            SELECT '{id}', id FROM registry.amenities WHERE code IN ('Balcony', 'SwimmingPool');
            """);

        await MigrateToLatestAsync();

        var rows = await QueryAsync(
            $"""
            SELECT a.code, a.ai_code
            FROM registry.property_amenities pa
            JOIN registry.amenities a ON a.id = pa.amenity_id
            WHERE pa.property_id = '{id}'
            ORDER BY a.code COLLATE "C";
            """);

        Assert.Equal(
            [["Balcony", "balcony"], ["SwimmingPool", "pool"]],
            rows.Select(row => row.Select(cell => cell?.ToString()).ToArray()).ToArray());
    }

    // ---- forward: the seed data

    [Fact]
    public async Task TheEightAiViewsAreSeededInDisplayOrder()
    {
        await MigrateToLatestFromScratchAsync();

        var rows = await QueryAsync("SELECT code, name_ar, sort_order FROM registry.views ORDER BY sort_order;");

        Assert.Equal(
            [
                ["City", "مدينة", 1], ["Community", "كومباوند", 2], ["Garden", "حديقة", 3], ["Golf", "جولف", 4],
                ["Nile", "نيل", 5], ["Pool", "حمام سباحة", 6], ["Sea", "بحر", 7], ["Street", "شارع", 8]
            ],
            rows.Select(row => row.ToArray()).ToArray());
    }

    [Fact]
    public async Task EveryAiAmenityHasAMatchingAmenityHere()
    {
        await MigrateToLatestFromScratchAsync();

        var codes = (await QueryAsync("SELECT DISTINCT ai_code COLLATE \"C\" AS ai_code FROM registry.amenities WHERE ai_code IS NOT NULL ORDER BY ai_code;"))
            .Select(row => (string)row[0]!)
            .ToArray();

        // The twenty codes of the AI service's own amenity list (GET /v1/options).
        Assert.Equal(
            [
                "balcony", "beach access", "central ac", "clubhouse", "concierge", "electricity meter", "elevator",
                "garden", "gym", "internet ready", "kids area", "kitchen", "maintenance service", "natural gas",
                "parking", "pool", "security", "smart home", "walking track", "water meter"
            ],
            codes);
    }

    [Theory]
    [InlineData("Balcony", "balcony")]
    [InlineData("Security", "security")]
    [InlineData("CentralAirConditioning", "central ac")]
    [InlineData("SwimmingPool", "pool")]
    [InlineData("PrivatePool", "pool")]
    [InlineData("PrivateGym", "gym")]
    [InlineData("SharedGym", "gym")]
    [InlineData("ChildrensPlayArea", "kids area")]
    [InlineData("CoveredParking", "parking")]
    [InlineData("ReceptionService", "concierge")]
    [InlineData("Garden", "garden")]
    [InlineData("Elevator", "elevator")]
    [InlineData("BeachAccess", "beach access")]
    [InlineData("SmartHome", "smart home")]
    [InlineData("InternetReady", "internet ready")]
    [InlineData("Kitchen", "kitchen")]
    public async Task EachAmenityCarriesItsAiCode(string code, string aiCode)
    {
        await MigrateToLatestFromScratchAsync();

        var row = await QuerySingleAsync($"SELECT ai_code FROM registry.amenities WHERE code = '{code}';");

        Assert.Equal([aiCode], row);
    }

    [Fact]
    public async Task AmenitiesWithNoAiEquivalentHaveNoAiCode()
    {
        await MigrateToLatestFromScratchAsync();

        var row = await QuerySingleAsync(
            "SELECT count(*) FROM registry.amenities WHERE code IN ('BarbecueArea', 'StudyRoom', 'MaidRoom', 'ConferenceRoom') AND ai_code IS NOT NULL;");

        Assert.Equal([0L], row);
    }

    [Fact]
    public async Task TheTwelveMissingAiAmenitiesAreAppendedAfterTheOriginalTwentyNine()
    {
        await MigrateToLatestFromScratchAsync();

        var rows = await QueryAsync("SELECT count(*), min(sort_order), max(sort_order) FROM registry.amenities WHERE sort_order >= 30;");

        Assert.Equal([12L, 30, 41], rows.Single().ToArray());
    }

    // ---- forward: the constraints

    [Theory]
    [InlineData("Unit")]
    [InlineData("Residential")]
    [InlineData("apartment")]
    public async Task TheOldAndInvalidTypesAreRefusedAfterTheMigration(string type)
    {
        await MigrateToLatestFromScratchAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertNewPropertyAsync(type: type));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_registry_properties_type", exception.ConstraintName);
    }

    [Theory]
    [InlineData("Furnished")]
    [InlineData("Unfinished")]
    [InlineData("SuperLux")]
    [InlineData("FullyFinished")]
    public async Task TheOldFinishingStatusesAreRefusedAfterTheMigration(string finishing)
    {
        await MigrateToLatestFromScratchAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertNewPropertyAsync(finishing: finishing));

        Assert.Equal("ck_registry_properties_finishing_status", exception.ConstraintName);
    }

    [Theory]
    [InlineData("Villa", "LuxuryFinished")]
    [InlineData("TwinHouse", "CoreAndShell")]
    [InlineData("Farm", null)]
    public async Task TheNewVocabularyIsAcceptedAfterTheMigration(string type, string? finishing)
    {
        await MigrateToLatestFromScratchAsync();

        await InsertNewPropertyAsync(type: type, finishing: finishing);
    }

    [Fact]
    public async Task TheMarketplaceConstraintAcceptsEverySixteenTypeNumberAndNoOther()
    {
        await MigrateToLatestFromScratchAsync();

        var definition = (string)(await QuerySingleAsync(
            """
            SELECT pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conname = 'ck_marketplace_submissions_content';
            """))[0]!;

        Assert.Contains("'16'", definition);
        Assert.DoesNotContain("'17'", definition);
    }

    // ---- backward

    [Fact]
    public async Task RollingBackRestoresTheOldVocabularyAndDropsTheNewTables()
    {
        // From scratch up to the migration under test only: AddNotifications refuses every automatic rollback.
        await MigrateToAsync(AiSearchMigration);
        var apartment = await InsertNewPropertyAsync(type: "Apartment", finishing: "LuxuryFinished", furnished: true);
        var villa = await InsertNewPropertyAsync(type: "Villa", finishing: "Finished");
        var office = await InsertNewPropertyAsync(type: "Office", finishing: "CoreAndShell");
        var land = await InsertNewPropertyAsync(type: "Land", finishing: "SemiFinished");

        await MigrateToAsync(Previous);

        Assert.Equal(("Unit", "Furnished"), await ReadOldAsync(apartment));
        Assert.Equal(("Unit", "FullyFinished"), await ReadOldAsync(villa));
        Assert.Equal(("Building", "Unfinished"), await ReadOldAsync(office));
        Assert.Equal(("Land", "SemiFinished"), await ReadOldAsync(land));

        Assert.Equal([false, false], await QuerySingleAsync(
            "SELECT to_regclass('registry.views') IS NOT NULL, to_regclass('registry.property_views') IS NOT NULL;"));
        Assert.Equal([0L], await QuerySingleAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ('price', 'furnished', 'ai_sync_status');"));
    }

    [Fact]
    public async Task RollingBackKeepsTheOriginalAmenitiesAndRemovesTheAddedOnes()
    {
        // From scratch up to the migration under test only: AddNotifications refuses every automatic rollback.
        await MigrateToAsync(AiSearchMigration);
        var id = await InsertNewPropertyAsync(type: "Apartment");
        await ExecuteAsync(
            $"""
            INSERT INTO registry.property_amenities (property_id, amenity_id)
            SELECT '{id}', id FROM registry.amenities WHERE code IN ('Balcony', 'Garden');
            """);

        await MigrateToAsync(Previous);

        Assert.Equal([29L], await QuerySingleAsync("SELECT count(*) FROM registry.amenities;"));
        Assert.Equal([1L], await QuerySingleAsync($"SELECT count(*) FROM registry.property_amenities WHERE property_id = '{id}';"));
        Assert.Equal([0L], await QuerySingleAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'amenities' AND column_name = 'ai_code';"));
    }

    [Fact]
    public async Task AfterRollingBackTheOldConstraintsApplyAgain()
    {
        // From scratch up to the migration under test only: AddNotifications refuses every automatic rollback.
        await MigrateToAsync(AiSearchMigration);
        await MigrateToAsync(Previous);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            INSERT INTO registry.properties (id, type, status, address_country_code, created_at, updated_at)
            VALUES (gen_random_uuid(), 'Villa', 'Active', 'EG', now(), now());
            """));

        Assert.Equal("ck_registry_properties_type", exception.ConstraintName);
    }

    [Fact]
    public async Task TheMigrationCanBeAppliedAgainAfterARollback()
    {
        // From scratch up to the migration under test only: AddNotifications refuses every automatic rollback.
        await MigrateToAsync(AiSearchMigration);
        await MigrateToAsync(Previous);
        var id = await InsertOldPropertyAsync("Unit", "Furnished");

        await MigrateToLatestAsync();

        Assert.Equal(("Apartment", "Finished", true), await ReadAsync(id));
        Assert.Equal([8L], await QuerySingleAsync("SELECT count(*) FROM registry.views;"));
        Assert.Equal([41L], await QuerySingleAsync("SELECT count(*) FROM registry.amenities;"));
    }

    // ---- helpers

    private async Task MigrateToAsync(string target)
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            // Only the first call of a test starts from nothing; later calls move the existing database.
            if (!await DatabaseHasMigrationHistoryAsync())
            {
                await deleting.Database.EnsureDeletedAsync();
            }
        }

        await using var context = Host.CreateVerificationContext();
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task MigrateToLatestAsync()
    {
        await using var context = Host.CreateVerificationContext();
        await context.Database.MigrateAsync();
    }

    private async Task MigrateToLatestFromScratchAsync()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await MigrateToLatestAsync();
    }

    private async Task<bool> DatabaseHasMigrationHistoryAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Host.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL;", connection);

            return (bool)(await command.ExecuteScalarAsync())!;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private async Task<Guid> InsertOldPropertyAsync(string type, string? finishing)
    {
        var id = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.properties
                (id, type, status, address_country_code, finishing_status, created_at, updated_at)
            VALUES (@id, @type, 'Active', 'EG', @finishing, @at, @at);
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("finishing", (object?)finishing ?? DBNull.Value);
        command.Parameters.AddWithValue("at", At);
        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<Guid> InsertNewPropertyAsync(string type = "Apartment", string? finishing = null, bool? furnished = null)
    {
        var id = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.properties
                (id, type, status, address_country_code, finishing_status, furnished, created_at, updated_at)
            VALUES (@id, @type, 'Active', 'EG', @finishing, @furnished, @at, @at);
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("finishing", (object?)finishing ?? DBNull.Value);
        command.Parameters.AddWithValue("furnished", (object?)furnished ?? DBNull.Value);
        command.Parameters.AddWithValue("at", At);
        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<(string Type, string? Finishing, bool? Furnished)> ReadAsync(Guid id)
    {
        var row = await QuerySingleAsync(
            $"SELECT type, finishing_status, furnished FROM registry.properties WHERE id = '{id}';");

        return ((string)row[0]!, (string?)row[1], (bool?)row[2]);
    }

    private async Task<(string Type, string? Finishing)> ReadOldAsync(Guid id)
    {
        var row = await QuerySingleAsync($"SELECT type, finishing_status FROM registry.properties WHERE id = '{id}';");

        return ((string)row[0]!, (string?)row[1]);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<List<object?>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<List<object?>>();
        while (await reader.ReadAsync())
        {
            var row = new List<object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row.Add(await reader.IsDBNullAsync(i) ? null : reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<List<object?>> QuerySingleAsync(string sql)
    {
        return (await QueryAsync(sql)).Single();
    }
}
