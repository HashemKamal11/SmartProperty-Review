using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Persistence.Context;
using Testcontainers.PostgreSql;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Owns the one PostgreSQL server this assembly runs against, and hands every test a private database on it.
/// </summary>
/// <remarks>
/// The server is an ephemeral Testcontainers container: a random host port, no named volume, no bind mount, and
/// credentials generated for this run only. Nothing about it is shared with the developer's own PostgreSQL, with
/// <c>docker-compose.yml</c>, or with any container this suite did not create. Testcontainers starts it, and
/// Testcontainers (through its resource reaper) removes it, so the tests never issue a Docker command.
///
/// Sharing the container does not mean sharing data. <see cref="CreateDatabaseAsync"/> gives each test a
/// freshly created database, so one test can never observe another's rows, and test order cannot matter.
///
/// The schema is built once, on a template database, by <c>Database.EnsureCreatedAsync()</c> against
/// the current EF model — the repository has no migrations, and this step adds none. Per-test databases are
/// then copies of that template, which PostgreSQL makes by cloning files rather than by replaying DDL.
/// </remarks>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    /// <summary>
    /// Matches the major version <c>docker-compose.yml</c> runs for local development, so the model is verified
    /// against the PostgreSQL the project actually targets.
    /// </summary>
    public const string PostgreSqlImage = "postgres:17";

    // The schema-bearing database every per-test database is copied from. Never written to after setup.
    private const string TemplateDatabase = "smartproperty_template";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(PostgreSqlImage)
        .WithDatabase("smartproperty_admin")
        .WithUsername("smartproperty_test")
        // Test-only credential for a container that exists for the length of one test run and is never
        // published. It is not a secret, and it is not the local development password.
        .WithPassword("smartproperty_test_password")
        .WithCleanUp(true)
        .Build();

    /// <summary>The image tag actually used, for reporting.</summary>
    public string Image => PostgreSqlImage;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        await ExecuteOnAdminDatabaseAsync($"""CREATE DATABASE "{TemplateDatabase}";""");

        var templateConnectionString = BuildConnectionString(TemplateDatabase);

        await using (var context = CreateContext(templateConnectionString))
        {
            await context.Database.EnsureCreatedAsync();
        }

        // PostgreSQL refuses to use a template that any session is still connected to, and a pooled connection
        // stays open after the context is disposed. Returning the pool to zero is what makes the copy possible.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(templateConnectionString));
    }

    public Task DisposeAsync()
    {
        return container.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Creates a private database for one test and returns its connection string.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // Hexadecimal only, so the identifier needs no escaping beyond the quoting already applied.
        var databaseName = $"sp_test_{Guid.NewGuid():n}";

        await ExecuteOnAdminDatabaseAsync($"""CREATE DATABASE "{databaseName}" TEMPLATE "{TemplateDatabase}";""");

        return BuildConnectionString(databaseName);
    }

    /// <summary>
    /// Returns a connection string for a database that does not exist on this container. Used to observe how an
    /// infrastructure failure surfaces, without stopping or removing anything.
    /// </summary>
    public string BuildMissingDatabaseConnectionString()
    {
        return BuildConnectionString($"sp_absent_{Guid.NewGuid():n}");
    }

    /// <summary>
    /// A standalone context, outside any service provider. Seeding and verification use these so that the
    /// interceptors a test installs on its own contexts observe only the operation under test.
    /// </summary>
    public static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    private string BuildConnectionString(string databaseName)
    {
        // Host and port come from Testcontainers, which mapped the container's 5432 to a free host port. No
        // host, port, or localhost address is written down anywhere in this suite.
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = databaseName,
            MaxPoolSize = 8
        }.ConnectionString;
    }

    private async Task ExecuteOnAdminDatabaseAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The single collection every test class joins, so one container serves the whole assembly.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}
