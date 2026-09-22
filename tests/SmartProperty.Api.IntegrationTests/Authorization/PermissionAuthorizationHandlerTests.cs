using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authorization;

/// <summary>
/// The bridge itself, driven through the real ASP.NET Core <see cref="IAuthorizationService"/> rather than over
/// HTTP, so resource typing, requirement combination, exception propagation, and cancellation can each be
/// observed directly.
/// </summary>
public sealed class PermissionAuthorizationHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakePermissionChecker _permissionChecker = new();
    private readonly DefaultHttpContext _httpContext = new();

    [Fact]
    public async Task AllowedDecision_SatisfiesTheRequirement()
    {
        _permissionChecker.Decision = AuthorizationDecision.Allowed;

        var result = await AuthorizeAsync(AuthorizationTarget.Platform, new PermissionRequirement("property.read"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, _permissionChecker.CallCount);
    }

    [Fact]
    public async Task DeniedDecision_LeavesTheRequirementUnsatisfied()
    {
        _permissionChecker.Decision = AuthorizationDecision.Denied;

        var result = await AuthorizeAsync(AuthorizationTarget.Platform, new PermissionRequirement("property.read"));

        Assert.False(result.Succeeded);
        Assert.Equal(1, _permissionChecker.CallCount);
    }

    [Fact]
    public async Task WorkspaceTarget_IsForwardedToTheChecker()
    {
        var workspaceId = Guid.NewGuid();
        _permissionChecker.Decision = AuthorizationDecision.Allowed;

        await AuthorizeAsync(
            AuthorizationTarget.Workspace(workspaceId),
            new PermissionRequirement("property.read"));

        Assert.Equal(workspaceId, _permissionChecker.LastRequest!.Target.WorkspaceId);
    }

    [Theory]
    [MemberData(nameof(WrongResources))]
    public async Task WrongResourceType_FailsWithoutAskingTheChecker(object? resource)
    {
        // The handler is typed to AuthorizationTarget, so the base class never invokes it for anything else.
        // An unexpected resource must leave the requirement unsatisfied, not fall through to success.
        _permissionChecker.Decision = AuthorizationDecision.Allowed;

        var result = await AuthorizeAsync(resource, new PermissionRequirement("property.read"));

        Assert.False(result.Succeeded);
        Assert.Equal(0, _permissionChecker.CallCount);
    }

    public static TheoryData<object?> WrongResources()
    {
        return new TheoryData<object?>
        {
            new object(),
            Guid.NewGuid(),
            "workspace",
            null
        };
    }

    [Fact]
    public async Task MultipleRequirements_AllMustBeSatisfied()
    {
        // ASP.NET Core combines requirements with AND. The bridge adds no OR logic of its own.
        _permissionChecker.AllowedPermissionCodes.Add("property.read");

        var result = await AuthorizeAsync(
            AuthorizationTarget.Platform,
            new PermissionRequirement("property.read"),
            new PermissionRequirement("property.delete"));

        Assert.False(result.Succeeded);
        Assert.Equal(2, _permissionChecker.CallCount);
        Assert.Equal(
            ["property.read", "property.delete"],
            _permissionChecker.Requests.Select(request => request.PermissionCode));
    }

    [Fact]
    public async Task MultipleRequirements_SucceedWhenEveryPermissionIsHeld()
    {
        _permissionChecker.AllowedPermissionCodes.Add("property.read");
        _permissionChecker.AllowedPermissionCodes.Add("property.delete");

        var result = await AuthorizeAsync(
            AuthorizationTarget.Platform,
            new PermissionRequirement("property.read"),
            new PermissionRequirement("property.delete"));

        Assert.True(result.Succeeded);
        Assert.Equal(2, _permissionChecker.CallCount);
    }

    [Fact]
    public async Task InfrastructureFailure_Propagates()
    {
        // A database outage is not an authorization answer: it must not be reported as Denied or as a 403.
        _permissionChecker.ExceptionToThrow = new InvalidOperationException("test persistence failure");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AuthorizeAsync(AuthorizationTarget.Platform, new PermissionRequirement("property.read")));

        Assert.Equal("test persistence failure", exception.Message);
    }

    [Fact]
    public async Task AbortedRequest_ForwardsTheRequestsOwnCancellationToken()
    {
        using var abortSource = new CancellationTokenSource();
        await abortSource.CancelAsync();
        _httpContext.RequestAborted = abortSource.Token;
        _permissionChecker.Decision = AuthorizationDecision.Allowed;

        await AuthorizeAsync(AuthorizationTarget.Platform, new PermissionRequirement("property.read"));

        Assert.Equal(abortSource.Token, _permissionChecker.LastCancellationToken);
        Assert.True(_permissionChecker.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task AbortedRequest_PropagatesACancelledChecker()
    {
        using var abortSource = new CancellationTokenSource();
        await abortSource.CancelAsync();
        _httpContext.RequestAborted = abortSource.Token;
        _permissionChecker.ExceptionToThrow = new OperationCanceledException(abortSource.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AuthorizeAsync(AuthorizationTarget.Platform, new PermissionRequirement("property.read")));
    }

    [Fact]
    public async Task UnauthenticatedPrincipal_FailsWithoutAskingTheChecker()
    {
        _permissionChecker.Decision = AuthorizationDecision.Allowed;

        var result = await AuthorizeAsync(
            AuthorizationTarget.Platform,
            currentUserId: null,
            new PermissionRequirement("property.read"));

        Assert.False(result.Succeeded);
        Assert.Equal(0, _permissionChecker.CallCount);
    }

    private Task<AuthorizationResult> AuthorizeAsync(
        object? resource,
        params PermissionRequirement[] requirements)
    {
        return AuthorizeAsync(resource, UserId, requirements);
    }

    private async Task<AuthorizationResult> AuthorizeAsync(
        object? resource,
        Guid? currentUserId,
        params PermissionRequirement[] requirements)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IPermissionChecker>(_permissionChecker);
        services.AddSingleton<ICurrentUser>(new StubCurrentUser(currentUserId));
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = _httpContext });
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        await using var provider = services.BuildServiceProvider();

        var authorizationService = provider.GetRequiredService<IAuthorizationService>();

        return await authorizationService.AuthorizeAsync(CreatePrincipal(currentUserId), resource, requirements);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid? userId)
    {
        if (userId is not { } subject)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, subject.ToString())],
            authenticationType: "TestBearer"));
    }

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public bool IsAuthenticated => UserId is not null;
    }
}
