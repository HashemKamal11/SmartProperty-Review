using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Persistence.Context;
using Testcontainers.PostgreSql;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Owns the isolated PostgreSQL database used by API integration-test hosts.
/// </summary>
public sealed class ApiPostgreSqlFixture : IAsyncLifetime
{
    private const string PostgreSqlImage = "postgres:17";
    private const string DatabaseEnvironmentVariable = "ConnectionStrings__Database";

    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly Dictionary<string, string?> previousEnvironment = [];
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(PostgreSqlImage)
        .WithDatabase("smartproperty_api_tests")
        .WithUsername("smartproperty_api_tests")
        .WithPassword("smartproperty_api_tests_password")
        .WithCleanUp(true)
        .Build();
    private bool initialized;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await EnsureInitializedAsync();
    }

    public string GetConnectionString()
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();

        return ConnectionString;
    }

    /// <summary>Creates a connectable database with no schema or migration history.</summary>
    public async Task<string> CreateBlankDatabaseAsync()
    {
        await EnsureInitializedAsync();

        var databaseName = $"sp_api_blank_{Guid.NewGuid():n}";

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand($"""CREATE DATABASE "{databaseName}";""", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = databaseName,
            MaxPoolSize = 16
        }.ConnectionString;
    }

    /// <summary>A valid server endpoint naming a database that does not exist.</summary>
    public string BuildMissingDatabaseConnectionString()
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();

        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"sp_api_absent_{Guid.NewGuid():n}",
            MaxPoolSize = 4,
            Timeout = 2,
            CommandTimeout = 2
        }.ConnectionString;
    }

    private async Task EnsureInitializedAsync()
    {
        if (initialized)
        {
            return;
        }

        await initializationLock.WaitAsync();
        try
        {
            if (initialized)
            {
                return;
            }

            await container.StartAsync();

            ConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                MaxPoolSize = 16
            }.ConnectionString;

            SetEnvironmentVariable(DatabaseEnvironmentVariable, ConnectionString);
            foreach (var setting in TestJwt.Configuration())
            {
                SetEnvironmentVariable(setting.Key.Replace(":", "__"), setting.Value);
            }

            await using var context = CreateContext();
            await context.Database.MigrateAsync();

            var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
            if (!appliedMigrations.Contains("20260923141641_InitialCreate", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The API integration-test database was not initialized from the current migration.");
            }

            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public Task DisposeAsync()
    {
        foreach (var environmentVariable in previousEnvironment)
        {
            Environment.SetEnvironmentVariable(environmentVariable.Key, environmentVariable.Value);
        }

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        }

        return container.DisposeAsync().AsTask();
    }

    private void SetEnvironmentVariable(string name, string? value)
    {
        previousEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// A standalone context on the test database, outside the API host's service provider. Test arrangement and
    /// verification use these so nothing is observed through a context a request under test is also using.
    /// </summary>
    public ApplicationDbContext CreateContext()
    {
        return CreateContext(ConnectionString);
    }

    /// <summary>A standalone context for another test-owned database on the same disposable server.</summary>
    public static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiPostgreSqlCollection : ICollectionFixture<ApiPostgreSqlFixture>
{
    public const string Name = "API PostgreSQL";
}
