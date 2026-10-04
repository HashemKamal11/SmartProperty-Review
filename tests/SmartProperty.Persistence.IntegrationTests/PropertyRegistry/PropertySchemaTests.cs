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
    public async Task TheRegistrySchemaHoldsOnlyThePropertiesTable()
    {
        var actual = await QueryStringsAsync(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'registry'
            ORDER BY 1;
            """);

        Assert.Equal(["properties"], actual);
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
            "created_at|timestamp with time zone|NO",
            "id|uuid|NO",
            "latitude|numeric|YES",
            "longitude|numeric|YES",
            "status|character varying(32)|NO",
            "type|character varying(32)|NO",
            "updated_at|timestamp with time zone|NO"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT a.attname || '|' || format_type(a.atttypid, a.atttypmod) || '|'
                   || CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END
            FROM pg_attribute a
            WHERE a.attrelid = 'registry.properties'::regclass AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attname;
            """);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, column => column.StartsWith("workspace_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, column => column.StartsWith("organisation_id|", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, column => column.StartsWith("tenant_id|", StringComparison.Ordinal));
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
            "ck_registry_properties_latitude_range",
            "ck_registry_properties_longitude_range",
            "ck_registry_properties_status",
            "ck_registry_properties_type"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'registry.properties'::regclass AND contype = 'c'
            ORDER BY 1;
            """);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ThePropertiesTableHasNoForeignKeyAndNoIndexBeyondItsPrimaryKey()
    {
        var foreignKeys = await QueryStringsAsync(
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'registry.properties'::regclass AND contype = 'f';
            """);

        var indexes = await QueryStringsAsync(
            """
            SELECT c.relname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE i.indrelid = 'registry.properties'::regclass AND NOT i.indisprimary;
            """);

        Assert.Empty(foreignKeys);
        Assert.Empty(indexes);
    }

    [Fact]
    public void NoPropertyColumnIsAConcurrencyToken()
    {
        using var context = Host.CreateVerificationContext();
        var entityType = context.Model.FindEntityType(typeof(Property))!;

        var tokens = entityType.GetProperties()
            .Concat(entityType.GetComplexProperties().SelectMany(complex => complex.ComplexType.GetProperties()))
            .Where(property => property.IsConcurrencyToken)
            .Select(property => property.Name);

        Assert.Empty(tokens);
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
