using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartProperty.Application.Abstractions.Persistence.Repositories;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real API host for the submitter reads, with a test-only stub in place of the PostgreSQL read repository and
/// test-controlled persisted accounts.
/// </summary>
/// <remarks>
/// <para>
/// The controller is public and discovered by MVC, and both handlers are the production registrations. With
/// <c>mount: true</c> only two services are replaced: the read repository, by the stub, and the user repository, by
/// <see cref="Accounts"/>. Authentication, the JWT bearer events, routing, model binding, the error handling and JSON
/// serialization are all the real production ones.
/// </para>
/// <para>
/// With <c>mount: false</c> the host is the production composition untouched, including the Persistence repository,
/// which is how the tests prove what production exposes. The connection string points nowhere: a test in that mode
/// must not send a request that reaches a handler.
/// </para>
/// </remarks>
internal sealed class SubmitterApiTestHost : IDisposable
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    private readonly SmartPropertyApiFactory _root;
    private readonly WebApplicationFactory<Program> _host;

    public SubmitterApiTestHost(StubSubmitterListingSubmissionReadRepository stub, bool mount = true)
    {
        Stub = stub;
        _root = new SmartPropertyApiFactory(UnusedConnectionString);
        _host = _root.WithWebHostBuilder(builder =>
        {
            // AddPersistence reads the connection string while Program is composed, so it is supplied as a host
            // setting. Nothing opens a connection with it.
            builder.UseSetting("ConnectionStrings:Database", UnusedConnectionString);

            if (!mount)
            {
                return;
            }

            builder.ConfigureTestServices(services =>
            {
                // The account behind a token is read from the user repository on every request; tests control it here.
                services.RemoveAll<IUserRepository>();
                services.AddSingleton<IUserRepository>(Accounts);

                // Only the repository is substituted; the controller and both handlers are the production ones.
                services.RemoveAll<ISubmitterListingSubmissionReadRepository>();
                services.AddSingleton<ISubmitterListingSubmissionReadRepository>(stub);
            });
        });
    }

    public StubSubmitterListingSubmissionReadRepository Stub { get; }

    /// <summary>The persisted accounts behind the tokens. Any user is Active unless a test sets otherwise.</summary>
    public StubUserRepository Accounts { get; } = new();

    public IServiceProvider Services => _host.Services;

    public HttpClient CreateClient() => _host.CreateClient();

    /// <summary>A client carrying a genuine, correctly signed access token for the given user.</summary>
    public HttpClient CreateClientFor(Guid userId)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.CreateAccessToken(userId));

        return client;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_host, _root))
        {
            _host.Dispose();
        }

        _root.Dispose();
    }
}
