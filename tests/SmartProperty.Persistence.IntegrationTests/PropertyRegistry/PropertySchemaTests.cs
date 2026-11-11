using Npgsql;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves the shape of <c>registry.properties</c>: one platform-global table holding the address inline, with
/// exactly the approved columns and nothing speculative alongside them.
/// </summary>
public sealed class PropertySchemaTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task TheRegistrySchemaHoldsOnlyTheApprovedTables()
    {
        var actual = await QueryStringsAsync(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'registry'
            ORDER BY 1;
            """);

        Assert.Equal(["amenities", "favorite_properties", "properties", "property_amenities", "property_comparison_items", "property_images", "property_views", "views"], actual);
    }

    [Fact]
    public async Task ThePropertiesTableHasExactlyTheApprovedColumns()
    {
        // name | type | nullable
        string[] expected =
        [
            "address_city|text|YES",
            "address_country_code|character varying(2)|NO",
            "address_district|text|YES",
            "address_line|text|YES",
            "address_postal_code|text|YES",
            "address_region|text|YES",
            "area_id|uuid|YES",
            "area_sqm|numeric(12,2)|YES",
            "bathrooms|integer|YES",
            "bedrooms|integer|YES",
            "built_date|date|YES",
            "compound_id|uuid|YES",
            "created_at|timestamp with time zone|NO",
            "finishing_status|character varying(32)|YES",
            "floor|integer|YES",
            "furnished|boolean|YES",
            "id|uuid|NO",
            "latitude|numeric|YES",
            "longitude|numeric|YES",
            "owner_user_id|uuid|YES",
            "status|character varying(32)|NO",
            "time_zone_id|character varying(64)|YES",
            "type|character varying(32)|NO",
            "updated_at|timestamp with time zone|NO"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT a.attname || '|' || format_type(a.atttypid, a.atttypmod) || '|'
                   || CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END
            FROM pg_attribute a
            WHERE a.attrelid = 'registry.properties'::regclass AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attname COLLATE "C";
            """);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, column => column.StartsWith("workspace_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, column => column.StartsWith("organisation_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, column => column.StartsWith("tenant_id|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLocationsSchemaHoldsTheFourPlaceTables()
    {
        var actual = await QueryStringsAsync(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'locations'
            ORDER BY table_name COLLATE "C";
            """);

        Assert.Equal(["areas", "cities", "compounds", "countries"], actual);
    }

    [Fact]
    public async Task ThePrimaryKeyIsTheIdColumn()
    {
        var actual = await QueryStringsAsync(
            """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = 'registry.properties'::regclass AND i.indisprimary;
            """);

        Assert.Equal(["id"], actual);
    }

    [Fact]
    public async Task ThePropertiesTableHasExactlyTheApprovedCheckConstraints()
    {
        string[] expected =
        [
            "ck_registry_properties_address_country_code",
            "ck_registry_properties_area_sqm_range",
            "ck_registry_properties_bathrooms_range",
            "ck_registry_properties_bedrooms_range",
            "ck_registry_properties_finishing_status",
            "ck_registry_properties_floor_range",
            "ck_registry_properties_latitude_range",
            "ck_registry_properties_longitude_range",
            "ck_registry_properties_place",
            "ck_registry_properties_status",
            "ck_registry_properties_type"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'registry.properties'::regclass AND contype = 'c'
            ORDER BY conname COLLATE "C";
            """);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ThePropertiesTableHasOnlyThePlaceAndOwnerForeignKeysAndTheirIndexes()
    {
        var foreignKeys = await QueryStringsAsync(
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'registry.properties'::regclass AND contype = 'f'
            ORDER BY conname COLLATE "C";
            """);

        var indexes = await QueryStringsAsync(
            """
            SELECT c.relname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE i.indrelid = 'registry.properties'::regclass AND NOT i.indisprimary
            ORDER BY c.relname COLLATE "C";
            """);

        Assert.Equal(["FK_properties_areas_area_id", "FK_properties_compounds_compound_id", "FK_properties_users_owner_user_id"], foreignKeys);
        Assert.Equal(["IX_properties_area_id", "IX_properties_compound_id", "IX_properties_owner_user_id"], indexes);
    }

    [Fact]
    public void StatusIsThePropertysOnlyConcurrencyToken()
    {
        using var context = Host.CreateVerificationContext();
        var entityType = context.Model.FindEntityType(typeof(Property))!;

        var tokens = entityType.GetProperties()
            .Concat(entityType.GetComplexProperties().SelectMany(complex => complex.ComplexType.GetProperties()))
            .Where(property => property.IsConcurrencyToken)
            .Select(property => property.Name);

        Assert.Equal([nameof(Property.Status)], tokens);
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
