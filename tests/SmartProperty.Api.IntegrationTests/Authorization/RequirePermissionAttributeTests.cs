using Microsoft.AspNetCore.Authorization;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authorization;

/// <summary>
/// What the attribute itself guarantees, independently of any endpoint using it.
/// </summary>
/// <remarks>
/// The behaviour over HTTP — the 401, the 403, and the exact permission and target handed to the checker — is
/// asserted against the real production endpoint in the administration endpoint tests. This file covers the two
/// things that are true before any request arrives: the requirement it builds, and that it makes the endpoint an
/// authenticated one.
/// </remarks>
public sealed class RequirePermissionAttributeTests
{
    [Fact]
    public void ThePermissionCodeIsTrimmedAndCasePreserved()
    {
        var attribute = new RequirePermissionAttribute("  Workspace.Access_Requests.Review  ");

        // Matches Permission.Code semantics: trimmed on the way in, compared exactly afterwards.
        Assert.Equal("Workspace.Access_Requests.Review", attribute.PermissionCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPermissionCodeThrows(string permissionCode)
    {
        // A requirement is server configuration, so a blank one is a programming error rather than an HTTP response.
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute(permissionCode));
    }

    [Fact]
    public void TheAttributeMakesTheEndpointRequireAuthentication()
    {
        var attribute = new RequirePermissionAttribute(PermissionCodes.WorkspaceAccessRequestsReview);

        // Being IAuthorizeData is what gets the unauthenticated caller the standard 401 from the authorization
        // middleware, and what the OpenAPI operation transformer reads to emit the Bearer security requirement.
        Assert.IsAssignableFrom<IAuthorizeData>(attribute);
        Assert.IsAssignableFrom<AuthorizeAttribute>(attribute);

        // No named policy: the permission travels as a requirement, not as a policy string to be looked up.
        Assert.Null(attribute.Policy);
    }

    [Fact]
    public void TheAttributeCarriesNoWorkspaceScope()
    {
        var attribute = new RequirePermissionAttribute(PermissionCodes.WorkspaceAccessRequestsReview);

        // Scope lives in the resource, not the attribute, and this attribute always supplies the platform target.
        Assert.Equal(PermissionCodes.WorkspaceAccessRequestsReview, attribute.PermissionCode);
        Assert.Null(AuthorizationTarget.Platform.WorkspaceId);
    }

    [Fact]
    public void ItIsTheProductionControllerThatDeclaresTheReviewPermission()
    {
        var attribute = typeof(Controllers.AdminWorkspaceAccessRequestsController)
            .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
            .Cast<RequirePermissionAttribute>()
            .Single();

        // Declared once on the controller, so no action can be added to it without the permission.
        Assert.Equal(PermissionCodes.WorkspaceAccessRequestsReview, attribute.PermissionCode);
    }
}
