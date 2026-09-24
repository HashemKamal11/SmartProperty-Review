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
/// The real API host, started in memory with an isolated test database and no developer machine configuration.
/// </summary>
/// <remarks>
/// Everything that decides the outcome of an authentication or authorization test stays production code:
/// routing, <c>CorrelationIdMiddleware</c>, JWT Bearer with its <c>OnTokenValidated</c>, <c>OnChallenge</c> and
/// <c>OnForbidden</c> events, the authorization stack including <c>PermissionAuthorizationHandler</c>, MVC
/// model binding, and JSON serialization. No fake authentication scheme is installed: a test that asserts the
/// 401 contract has to go through the real bearer handler for that assertion to mean anything.
///
/// Only <see cref="IPermissionChecker"/> is replaced, because these tests assert the HTTP authentication and
/// authorization boundary rather than permission graph queries. EF Core stays registered exactly as production
/// registers it, pointed at the collection fixture's migrated PostgreSQL database.
/// </remarks>
internal sealed class SmartPropertyApiFactory : WebApplicationFactory<Program>
{
    private readonly string databaseConnectionString;

    public SmartPropertyApiFactory(ApiPostgreSqlFixture database)
    {
        ArgumentNullException.ThrowIfNull(database);

        databaseConnectionString = database.GetConnectionString();
    }

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
                new KeyValuePair<string, string?>("ConnectionStrings:Database", databaseConnectionString),
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
