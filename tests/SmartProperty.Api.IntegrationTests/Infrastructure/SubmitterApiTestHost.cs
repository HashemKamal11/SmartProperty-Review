using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Controllers;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real API host with the submitter controller mounted explicitly, its handlers registered, and a test-only
/// stub in place of the read repository that has no production implementation yet.
/// </summary>
/// <remarks>
/// <para>
/// Production does not expose this controller: it is internal, so MVC does not discover it, and nothing registers its
/// handlers. Mounting it takes two test-only steps that never exist in production: a feature provider that adds the
/// internal controller type, and the registrations. Authentication, the JWT bearer events, routing, model binding,
/// the error handling and JSON serialization are all the real production ones.
/// </para>
/// <para>
/// With <c>mount: false</c> the host is the production composition untouched, which is how the tests prove the route
/// does not exist there. The connection string points nowhere and nothing opens a connection.
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
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new MountedControllerFeatureProvider(typeof(SubmitterListingSubmissionsController))));

                services.AddSingleton<ISubmitterListingSubmissionReadRepository>(stub);
                services.AddScoped<IQueryHandler<GetMyListingSubmissionsQuery, PagedList<SubmitterSubmissionSummary>>, GetMyListingSubmissionsQueryHandler>();
                services.AddScoped<IQueryHandler<GetMyListingSubmissionByIdQuery, SubmitterSubmissionDetail>, GetMyListingSubmissionByIdQueryHandler>();
            });
        });
    }

    public StubSubmitterListingSubmissionReadRepository Stub { get; }

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

    // MVC only discovers public controllers; this adds one internal type, for tests only.
    private sealed class MountedControllerFeatureProvider(Type controllerType) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var typeInfo = controllerType.GetTypeInfo();

            if (!feature.Controllers.Contains(typeInfo))
            {
                feature.Controllers.Add(typeInfo);
            }
        }
    }
}
