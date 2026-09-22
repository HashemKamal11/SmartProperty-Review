using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartProperty.Application.Abstractions.Authorization;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real API host, started in memory with no database and no developer machine configuration.
/// </summary>
/// <remarks>
/// Everything that decides the outcome of an authentication or authorization test stays production code:
/// routing, <c>CorrelationIdMiddleware</c>, JWT Bearer with its <c>OnTokenValidated</c>, <c>OnChallenge</c> and
/// <c>OnForbidden</c> events, the authorization stack including <c>PermissionAuthorizationHandler</c>, MVC
/// model binding, and JSON serialization. No fake authentication scheme is installed: a test that asserts the
/// 401 contract has to go through the real bearer handler for that assertion to mean anything.
///
/// Only <see cref="IPermissionChecker"/> is replaced, because it is the one seam that would otherwise reach
/// PostgreSQL. EF Core stays registered exactly as production registers it, pointed at a connection string
/// that names no reachable server: <c>AddDbContext</c> opens nothing, and no route these tests exercise runs a
/// query. The database health check is registered as in production and is never invoked, because
/// <c>/health/ready</c> is not among the routes under test.
/// </remarks>
internal sealed class SmartPropertyApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Syntactically valid and deliberately unreachable. Npgsql connects lazily, so this is never dialled;
    /// it exists only because <c>AddPersistence</c> refuses to start without a connection string.
    /// </summary>
    private const string UnreachableConnectionString =
        "Host=no-such-host.smartproperty.invalid;Database=smartproperty_tests_no_database;" +
        "Username=tests;Password=tests;Timeout=1";

    public FakePermissionChecker PermissionChecker { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration(configuration =>
        {
            // Added last so it wins over appsettings.json, and so nothing depends on User Secrets, an
            // appsettings.Local file, or an environment variable on the machine running the suite.
            configuration.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("ConnectionStrings:Database", UnreachableConnectionString),
                .. TestJwt.Configuration()
            ]);
        });

        builder.ConfigureTestServices(services =>
        {
            // The one seam that would otherwise query PostgreSQL. Singleton so the instance the test inspects
            // is the instance the request-scoped authorization handler resolves.
            services.RemoveAll<IPermissionChecker>();
            services.AddSingleton<IPermissionChecker>(PermissionChecker);

            // Makes the test-only route discoverable in this host. It is declared in the test assembly and
            // reaches MVC only here, so it cannot exist in the production application.
            services.AddControllers()
                .AddApplicationPart(typeof(TestPermissionController).Assembly);
        });
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
