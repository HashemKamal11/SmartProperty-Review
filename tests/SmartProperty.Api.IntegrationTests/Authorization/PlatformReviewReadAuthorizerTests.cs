using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authorization;

/// <summary>
/// The review read authorizer against a recording stand-in for <see cref="IPermissionChecker"/>. No database is
/// involved: what the persistence-backed checker answers for inactive users or missing grants is its own contract,
/// so here "suspended or inactive" is whatever the checker reports, which must be a denial and nothing else.
/// </summary>
public sealed class PlatformReviewReadAuthorizerTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    private static readonly Guid Reviewer = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly FakePermissionChecker _checker = new();

    [Fact]
    public async Task AllowedByTheCheckerIsAuthorized()
    {
        _checker.Decision = AuthorizationDecision.Allowed;

        Assert.True(await CreateAuthorizer().IsAuthorizedAsync(Reviewer));
    }

    [Fact]
    public async Task DeniedByTheCheckerIsNotAuthorized()
    {
        _checker.Decision = AuthorizationDecision.Denied;

        Assert.False(await CreateAuthorizer().IsAuthorizedAsync(Reviewer));
    }

    [Fact]
    public async Task ASuspendedOrInactiveUserTheCheckerDeniesIsNotAuthorized()
    {
        // PermissionChecker answers Denied for a user that is not Active, whatever roles they hold.
        _checker.Decision = AuthorizationDecision.Denied;

        Assert.False(await CreateAuthorizer().IsAuthorizedAsync(Reviewer));
    }

    [Fact]
    public async Task EachCallAsksTheCheckerExactlyOnceForTheReadPermissionAtPlatformScopeForThatUserOnly()
    {
        _checker.Decision = AuthorizationDecision.Allowed;

        await CreateAuthorizer().IsAuthorizedAsync(Reviewer);

        var request = Assert.Single(_checker.Requests);
        Assert.Equal(Reviewer, request.UserId);
        Assert.Equal("marketplace.submissions.review.read", request.PermissionCode);
        Assert.Equal(PermissionCodes.MarketplaceSubmissionsReviewRead, request.PermissionCode);
        Assert.Same(AuthorizationTarget.Platform, request.Target);
        Assert.Equal(RoleScope.Platform, request.Target.Scope);
        Assert.Null(request.Target.WorkspaceId);
    }

    [Fact]
    public async Task OnlyTheReadPermissionAuthorizesAndTheDecidePermissionAloneDoesNot()
    {
        _checker.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewDecide);

        Assert.False(await CreateAuthorizer().IsAuthorizedAsync(Reviewer));
        Assert.DoesNotContain(_checker.Requests, request => request.PermissionCode == PermissionCodes.MarketplaceSubmissionsReviewDecide);
    }

    [Fact]
    public async Task TheReadPermissionAloneAuthorizesWithoutAnyOtherPermission()
    {
        _checker.AllowedPermissionCodes.Add(PermissionCodes.MarketplaceSubmissionsReviewRead);

        Assert.True(await CreateAuthorizer().IsAuthorizedAsync(Reviewer));
    }

    [Theory]
    [InlineData("workspace.read")]
    [InlineData("workspace.access_requests.review")]
    [InlineData("property.read")]
    [InlineData("property.update")]
    [InlineData("marketplace.submissions.review.decide")]
    public async Task AnyOtherPermissionDoesNotAuthorize(string otherCode)
    {
        _checker.AllowedPermissionCodes.Add(otherCode);

        Assert.False(await CreateAuthorizer().IsAuthorizedAsync(Reviewer));
    }

    [Fact]
    public async Task ACheckerFailureIsNeverTurnedIntoAnAnswer()
    {
        _checker.Decision = AuthorizationDecision.Allowed;
        _checker.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAuthorizer().IsAuthorizedAsync(Reviewer));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task CancellationPropagatesAndIsNeverTurnedIntoAnAnswer()
    {
        _checker.ExceptionToThrow = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateAuthorizer().IsAuthorizedAsync(Reviewer));
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheChecker()
    {
        using var source = new CancellationTokenSource();
        _checker.Decision = AuthorizationDecision.Allowed;

        await CreateAuthorizer().IsAuthorizedAsync(Reviewer, source.Token);

        Assert.Equal(source.Token, _checker.LastCancellationToken);
    }

    [Fact]
    public async Task AnEmptyReviewerIdIsRejectedWithoutAskingTheChecker()
    {
        _checker.Decision = AuthorizationDecision.Allowed;

        await Assert.ThrowsAsync<ArgumentException>(() => CreateAuthorizer().IsAuthorizedAsync(Guid.Empty));

        Assert.Equal(0, _checker.CallCount);
    }

    [Fact]
    public void TheAuthorizerDependsOnTheCheckerAbstractionAndNothingElse()
    {
        var constructor = Assert.Single(typeof(PlatformReviewReadAuthorizer).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(IPermissionChecker), parameter.ParameterType);
        Assert.True(typeof(IReviewReadAuthorizer).IsAssignableFrom(typeof(PlatformReviewReadAuthorizer)));
        Assert.True(typeof(PlatformReviewReadAuthorizer).IsSealed);
    }

    [Fact]
    public void NothingInTheApplicationAssemblyImplementsTheSeam()
    {
        var implementations = typeof(IReviewReadAuthorizer).Assembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(IReviewReadAuthorizer).IsAssignableFrom(type))
            .ToArray();

        Assert.DoesNotContain(implementations, type => type is not null);
    }

    [Fact]
    public void TheProductionContainerRegistersNeitherTheAuthorizerNorTheReviewHandlers()
    {
        using var root = new SmartPropertyApiFactory(UnusedConnectionString);
        using var host = root.WithWebHostBuilder(builder =>
        {
            // AddPersistence reads the connection string while Program is composed, so it is supplied as a host
            // setting. Nothing opens a connection with it.
            builder.UseSetting("ConnectionStrings:Database", UnusedConnectionString);
        });
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.Null(provider.GetService<IReviewReadAuthorizer>());
        Assert.Null(provider.GetService<PlatformReviewReadAuthorizer>());
        Assert.Null(provider.GetService<IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>>>());
        Assert.Null(provider.GetService<IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail>>());
        Assert.Null(provider.GetService<GetReviewQueueQueryHandler>());
        Assert.Null(provider.GetService<GetReviewSubmissionQueryHandler>());
    }

    private PlatformReviewReadAuthorizer CreateAuthorizer()
    {
        return new PlatformReviewReadAuthorizer(_checker);
    }
}
