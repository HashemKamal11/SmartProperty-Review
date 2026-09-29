using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real API host, started in memory with an isolated test database and no developer machine configuration.
/// </summary>
/// <remarks>
/// Everything that decides the outcome of an authentication or authorization test stays production code:
/// routing, <c>CorrelationIdMiddleware</c>, JWT Bearer with its <c>OnTokenValidated</c>, <c>OnChallenge</c> and
/// <c>OnForbidden</c> events, the authorization stack including <c>PermissionAuthorizationHandler</c>, MVC
/// model binding, and JSON serialization. No fake authentication scheme is installed: a test that asserts the
/// 401 contract has to go through the real bearer handler for that assertion to mean anything.
///
/// By default only <see cref="IPermissionChecker"/> is replaced, because most of these tests assert the HTTP
/// authentication and authorization boundary rather than permission graph queries. EF Core stays registered
/// exactly as production registers it, pointed at the collection fixture's migrated PostgreSQL database.
///
/// A test that is about the access graph itself — who really holds a platform permission, and whether a workspace
/// grant can satisfy a platform target — asks for <c>usePersistencePermissionChecker</c> and then nothing at all is
/// substituted: the production <c>PermissionChecker</c> answers from the same PostgreSQL rows the test seeded.
/// </remarks>
internal sealed class SmartPropertyApiFactory : WebApplicationFactory<Program>
{
    private readonly string databaseConnectionString;
    private readonly bool usePersistencePermissionChecker;
    private readonly KeyValuePair<string, string?>[] extraConfiguration;
    private readonly string hostEnvironment;

    /// <param name="database">The collection fixture's migrated PostgreSQL database.</param>
    /// <param name="usePersistencePermissionChecker">
    /// True to leave the production permission checker in place and resolve permissions from the database.
    /// </param>
    /// <param name="configuration">
    /// Extra configuration for this host only, applied last. Used to exercise configuration-driven startup
    /// behaviour such as the platform administrator bootstrap; every value is supplied by the test.
    /// </param>
    /// <param name="environment">Host environment. Defaults to Development for existing OpenAPI coverage.</param>
    public SmartPropertyApiFactory(
        ApiPostgreSqlFixture database,
        bool usePersistencePermissionChecker = false,
        IEnumerable<KeyValuePair<string, string?>>? configuration = null,
        string? environment = null)
        : this(
            database?.GetConnectionString()
                ?? throw new ArgumentNullException(nameof(database)),
            usePersistencePermissionChecker,
            configuration,
            environment)
    {
    }

    /// <param name="databaseConnectionString">
    /// A test-owned database, including an intentionally blank database used by deployment-readiness tests.
    /// </param>
    public SmartPropertyApiFactory(
        string databaseConnectionString,
        bool usePersistencePermissionChecker = false,
        IEnumerable<KeyValuePair<string, string?>>? configuration = null,
        string? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseConnectionString);

        this.databaseConnectionString = databaseConnectionString;
        this.usePersistencePermissionChecker = usePersistencePermissionChecker;
        extraConfiguration = configuration?.ToArray() ?? [];
        hostEnvironment = environment ?? Environments.Development;
    }

    /// <summary>
    /// The substituted checker. Never consulted by a host built with <c>usePersistencePermissionChecker</c>, where
    /// the production checker answers instead.
    /// </summary>
    public FakePermissionChecker PermissionChecker { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(hostEnvironment);

        builder.ConfigureAppConfiguration(configuration =>
        {
            // Added last so it wins over appsettings.json, and so nothing depends on User Secrets, an
            // appsettings.Local file, or an environment variable on the machine running the suite.
            configuration.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("ConnectionStrings:Database", databaseConnectionString),
                .. TestJwt.Configuration(),
                .. extraConfiguration
            ]);
        });

        builder.ConfigureTestServices(services =>
        {
            // AddPersistence reads the connection string while Program is being composed, before this factory's
            // late app-configuration callback can replace an environment value. Replace the complete DbContext
            // registration so hosts aimed at blank, pending, and unavailable test databases really use the
            // caller-supplied database. Provider and scoped lifetime remain identical to production.
            var contextRegistrations = services
                .Where(descriptor => DescribesApplicationDbContext(descriptor.ServiceType))
                .ToList();

            foreach (var registration in contextRegistrations)
            {
                services.Remove(registration);
            }

            services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(databaseConnectionString));

            if (!usePersistencePermissionChecker)
            {
                // The one seam that would otherwise query PostgreSQL. Singleton so the instance the test inspects
                // is the instance the request-scoped authorization handler resolves.
                services.RemoveAll<IPermissionChecker>();
                services.AddSingleton<IPermissionChecker>(PermissionChecker);
            }

            // Makes the test-only route discoverable in this host. It is declared in the test assembly and
            // reaches MVC only here, so it cannot exist in the production application.
            services.AddControllers()
                .AddApplicationPart(typeof(TestPermissionController).Assembly);
        });
    }

    private static bool DescribesApplicationDbContext(Type serviceType)
    {
        return serviceType == typeof(ApplicationDbContext)
            || serviceType == typeof(DbContextOptions)
            || (serviceType.IsGenericType
                && Array.IndexOf(serviceType.GetGenericArguments(), typeof(ApplicationDbContext)) >= 0);
    }

    /// <summary>An <c>HttpClient</c> whose requests carry a valid access token for <paramref name="userId"/>.</summary>
    public HttpClient CreateAuthenticatedClient(Guid userId)
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwt.CreateAccessToken(userId));

        return client;
    }
}
