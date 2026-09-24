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

    private ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiPostgreSqlCollection : ICollectionFixture<ApiPostgreSqlFixture>
{
    public const string Name = "API PostgreSQL";
}
