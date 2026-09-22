using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Base for every test that needs a database. xUnit constructs one instance per test method, so each test gets
/// its own database and its own service provider, and no test can see another's rows.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public abstract class DatabaseTest(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private PersistenceTestHost? host;

    protected PostgreSqlFixture Fixture { get; } = fixture;

    internal PersistenceTestHost Host => host
        ?? throw new InvalidOperationException("The test host is only available between InitializeAsync and DisposeAsync.");

    public virtual async Task InitializeAsync()
    {
        host = await PersistenceTestHost.CreateAsync(Fixture, ExtraInterceptors());
    }

    public virtual async Task DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>Interceptors this test needs in addition to the command counter, such as a save barrier.</summary>
    private protected virtual IInterceptor[] ExtraInterceptors()
    {
        return [];
    }
}
