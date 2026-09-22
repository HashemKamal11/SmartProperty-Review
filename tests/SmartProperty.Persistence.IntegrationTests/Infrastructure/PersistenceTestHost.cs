using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Logout;
using SmartProperty.Application.Authentication.Refresh;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// One test's private database plus a service provider built from the production
/// <c>AddPersistence</c> registrations.
/// </summary>
/// <remarks>
/// Going through <c>AddPersistence</c> is deliberate: <c>UnitOfWork</c>, the repositories, and
/// <c>PermissionChecker</c> are internal to SmartProperty.Persistence, and resolving them from the container is
/// how production reaches them too. Nothing in the production assembly is made public or visible for testing.
///
/// The only registration this replaces is the <see cref="ApplicationDbContext"/> one, and only so a test can
/// attach its own interceptors — <c>AddPersistence</c> offers no seam for them. The replacement uses the same
/// provider, the same connection string, and the same scoped lifetime; it changes what observes the context,
/// never how the context behaves.
/// </remarks>
internal sealed class PersistenceTestHost : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private PersistenceTestHost(
        string connectionString,
        ServiceProvider services,
        DbCommandCounterInterceptor commands,
        TestClock clock,
        TestTokenProvider tokens)
    {
        ConnectionString = connectionString;
        this.services = services;
        Commands = commands;
        Clock = clock;
        Tokens = tokens;
    }

    public string ConnectionString { get; }

    /// <summary>Counts the commands issued by contexts resolved from this host.</summary>
    public DbCommandCounterInterceptor Commands { get; }

    public TestClock Clock { get; }

    public TestTokenProvider Tokens { get; }

    public static async Task<PersistenceTestHost> CreateAsync(
        PostgreSqlFixture fixture,
        params IInterceptor[] interceptors)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var connectionString = await fixture.CreateDatabaseAsync();

        return Create(connectionString, interceptors);
    }

    /// <summary>
    /// Builds a host over a caller-supplied connection string, including one that points at a database that does
    /// not exist.
    /// </summary>
    public static PersistenceTestHost Create(string connectionString, params IInterceptor[] interceptors)
    {
        var commands = new DbCommandCounterInterceptor();
        var clock = new TestClock();
        var tokens = new TestTokenProvider(clock);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString
            })
            .Build();

        var services = new ServiceCollection();

        services.AddPersistence(configuration);

        ReplaceDbContextRegistration(services, connectionString, [commands, .. interceptors]);

        services.AddSingleton<IDateTimeProvider>(clock);
        services.AddSingleton<ITokenProvider>(tokens);
        services.AddScoped<RefreshCommandHandler>();
        services.AddScoped<LogoutCommandHandler>();

        return new PersistenceTestHost(connectionString, services.BuildServiceProvider(), commands, clock, tokens);
    }

    /// <summary>
    /// A scope standing in for one request. Two scopes hold two independent
    /// <see cref="ApplicationDbContext"/> instances, which is what the concurrency tests need.
    /// </summary>
    public IServiceScope CreateScope()
    {
        return services.CreateScope();
    }

    /// <summary>
    /// A context outside the container, used to seed and to verify. It carries none of the test's interceptors,
    /// so setup never reaches the command counter or the save barrier.
    /// </summary>
    public ApplicationDbContext CreateVerificationContext()
    {
        return PostgreSqlFixture.CreateContext(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();

        // The database stays behind on a container that is about to be destroyed, but its pooled connections
        // would not: returning them keeps the server well inside its connection limit across a long run.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
    }

    // AddDbContext registers the context, its options, and the options-configuration callback. Each of those is
    // removed by service type before the replacement is added, so the result never depends on whether EF used
    // TryAdd for a particular one.
    private static void ReplaceDbContextRegistration(
        IServiceCollection services,
        string connectionString,
        IInterceptor[] interceptors)
    {
        var registrations = services
            .Where(descriptor => DescribesTheContext(descriptor.ServiceType))
            .ToList();

        foreach (var registration in registrations)
        {
            services.Remove(registration);
        }

        services.AddDbContext<ApplicationDbContext>(options => options
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptors));
    }

    private static bool DescribesTheContext(Type serviceType)
    {
        return serviceType == typeof(ApplicationDbContext)
            || serviceType == typeof(DbContextOptions)
            || (serviceType.IsGenericType
                && Array.IndexOf(serviceType.GetGenericArguments(), typeof(ApplicationDbContext)) >= 0);
    }
}
