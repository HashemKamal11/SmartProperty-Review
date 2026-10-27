using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Controllers;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The real API host with the internal Platform Admin review controller mounted explicitly, a test-only stub in place
/// of the reviewer read repository, and the real <c>PlatformReviewReadAuthorizer</c> over a recording permission
/// checker.
/// </summary>
/// <remarks>
/// With <c>mount: false</c> it is the production composition plus the stub's mere existence, which nothing registers:
/// the controller is not discovered and none of the review services are resolvable. Nothing here opens a database
/// connection.
/// </remarks>
internal sealed class AdminReviewApiTestHost : IDisposable
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    private readonly SmartPropertyApiFactory _root;
    private readonly WebApplicationFactory<Program> _host;

    public AdminReviewApiTestHost(StubReviewerListingSubmissionReadRepository stub, bool mount = true)
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
                    manager.FeatureProviders.Add(new MountedControllerFeatureProvider(typeof(AdminListingSubmissionReviewController))));

                services.AddSingleton<IReviewerListingSubmissionReadRepository>(stub);
                services.AddScoped<IReviewReadAuthorizer, PlatformReviewReadAuthorizer>();
                services.AddScoped<IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>>, GetReviewQueueQueryHandler>();
                services.AddScoped<IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail>, GetReviewSubmissionQueryHandler>();
            });
        });
    }

    public StubReviewerListingSubmissionReadRepository Stub { get; }

    /// <summary>The one authorization seam; tests decide what it allows and read what it was asked.</summary>
    public FakePermissionChecker Permissions => _root.PermissionChecker;

    public IServiceProvider Services => _host.Services;

    public HttpClient CreateClient() => _host.CreateClient();

    public HttpClient CreateClientFor(Guid userId)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.CreateAccessToken(userId));

        return client;
    }

    /// <summary>A client for a user who holds exactly the review read permission at Platform scope.</summary>
    public HttpClient CreateReviewerClientFor(Guid userId)
    {
        Permissions.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);

        return CreateClientFor(userId);
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
