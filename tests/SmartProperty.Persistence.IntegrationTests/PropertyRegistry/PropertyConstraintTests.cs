using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Verifies that PostgreSQL itself protects the stored property invariants, so a row written around the domain,
/// by direct SQL, cannot hold a value the <c>Property</c> aggregate would refuse.
/// </summary>
/// <remarks>
/// Every row here is written with plain Npgsql, deliberately bypassing the domain and the EF value converters.
/// The country code check is a format check only: an unassigned but well-formed code such as <c>ZZ</c> is
/// accepted, because country membership is not the database's concern.
/// </remarks>
public sealed class PropertyConstraintTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task AValidRowIsAccepted()
    {
        await InsertAsync();

        Assert.Equal(1L, await CountAsync());
    }

    [Theory]
    [InlineData("SA")]
    [InlineData("ZZ")]
    public async Task AnyTwoUppercaseAsciiLettersAreAcceptedAsACountryCode(string countryCode)
    {
        await InsertAsync(countryCode: countryCode);

        Assert.Equal(1L, await CountAsync());
    }

    [Theory]
    [InlineData("sa")]
    [InlineData("Sa")]
    [InlineData("S")]
    [InlineData("")]
    [InlineData("S1")]
    [InlineData("12")]
    [InlineData("S ")]
    [InlineData("ÄB")]
    public async Task AMalformedCountryCodeIsRejected(string countryCode)
    {
        await AssertCheckViolationAsync(
            "ck_registry_properties_address_country_code",
            () => InsertAsync(countryCode: countryCode));
    }

    [Fact]
    public async Task ACountryCodeLongerThanTwoCharactersIsRejected()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(countryCode: "SAU"));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, exception.SqlState);
    }

    [Fact]
    public async Task AMissingCountryCodeIsRejected()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(countryCode: null));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, exception.SqlState);
        Assert.Equal("address_country_code", exception.ColumnName);
    }

    [Theory]
    [InlineData("Land")]
    [InlineData("Building")]
    [InlineData("Unit")]
    public async Task EverySupportedTypeIsAccepted(string type)
    {
        await InsertAsync(type: type);

        Assert.Equal(1L, await CountAsync());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("land")]
    [InlineData("Residential")]
    [InlineData("")]
    public async Task AnUnsupportedTypeIsRejected(string type)
    {
        await AssertCheckViolationAsync("ck_registry_properties_type", () => InsertAsync(type: type));
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Archived")]
    public async Task EverySupportedStatusIsAccepted(string status)
    {
        await InsertAsync(status: status);

        Assert.Equal(1L, await CountAsync());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("active")]
    [InlineData("Deleted")]
    [InlineData("")]
    public async Task AnUnsupportedStatusIsRejected(string status)
    {
        await AssertCheckViolationAsync("ck_registry_properties_status", () => InsertAsync(status: status));
    }

    [Theory]
    [InlineData("-90.000001")]
    [InlineData("90.000001")]
    [InlineData("-180")]
    [InlineData("180")]
    public async Task ALatitudeOutsideItsRangeIsRejected(string latitude)
    {
        await AssertCheckViolationAsync(
            "ck_registry_properties_latitude_range",
            () => InsertAsync(latitude: Parse(latitude)));
    }

    [Theory]
    [InlineData("-180.000001")]
    [InlineData("180.000001")]
    [InlineData("-360")]
    [InlineData("360")]
    public async Task ALongitudeOutsideItsRangeIsRejected(string longitude)
    {
        await AssertCheckViolationAsync(
            "ck_registry_properties_longitude_range",
            () => InsertAsync(longitude: Parse(longitude)));
    }

    [Theory]
    [InlineData("-90", "-180")]
    [InlineData("90", "180")]
    [InlineData("0", "0")]
    [InlineData("-90", null)]
    [InlineData("90", null)]
    [InlineData(null, "-180")]
    [InlineData(null, "180")]
    [InlineData(null, null)]
    public async Task InclusiveBoundaryAndIndependentlyNullCoordinatesAreAccepted(string? latitude, string? longitude)
    {
        await InsertAsync(latitude: Parse(latitude), longitude: Parse(longitude));

        Assert.Equal(1L, await CountAsync());
    }

    private static decimal? Parse(string? value)
    {
        return value is null ? null : decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task AssertCheckViolationAsync(string constraintName, Func<Task> insert)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(insert);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private async Task InsertAsync(
        string? type = "Land",
        string? status = "Active",
        string? countryCode = "SA",
        decimal? latitude = null,
        decimal? longitude = null)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.properties
                (id, type, status, address_country_code, latitude, longitude, created_at, updated_at)
            VALUES
                (@id, @type, @status, @country_code, @latitude, @longitude, @created_at, @created_at);
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.Add(Text("type", type));
        command.Parameters.Add(Text("status", status));
        command.Parameters.Add(Text("country_code", countryCode));
        command.Parameters.Add(Numeric("latitude", latitude));
        command.Parameters.Add(Numeric("longitude", longitude));
        command.Parameters.AddWithValue("created_at", TestClock.DefaultNow);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync()
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM registry.properties;", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static NpgsqlParameter Text(string name, string? value)
    {
        return new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
    }

    private static NpgsqlParameter Numeric(string name, decimal? value)
    {
        return new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = (object?)value ?? DBNull.Value };
    }
}
