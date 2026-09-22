using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authorization;

public sealed class AuthorizationRequestTests
{
    [Fact]
    public void For_KeepsTheUserPermissionAndTarget()
    {
        var userId = Guid.NewGuid();

        var request = AuthorizationRequest.For(userId, "property.read", AuthorizationTarget.Platform);

        Assert.Equal(userId, request.UserId);
        Assert.Equal("property.read", request.PermissionCode);
        Assert.Same(AuthorizationTarget.Platform, request.Target);
    }

    [Fact]
    public void For_TrimsThePermissionCode()
    {
        var request = AuthorizationRequest.For(Guid.NewGuid(), "  property.read  ", AuthorizationTarget.Platform);

        Assert.Equal("property.read", request.PermissionCode);
    }

    [Fact]
    public void For_PreservesPermissionCodeCasing()
    {
        // Permission codes are compared exactly; normalizing case here would silently widen every check.
        var request = AuthorizationRequest.For(Guid.NewGuid(), "Property.Read", AuthorizationTarget.Platform);

        Assert.Equal("Property.Read", request.PermissionCode);
    }

    [Fact]
    public void For_AcceptsAWorkspaceTarget()
    {
        var workspaceId = Guid.NewGuid();
        var target = AuthorizationTarget.Workspace(workspaceId);

        var request = AuthorizationRequest.For(Guid.NewGuid(), "property.read", target);

        Assert.Equal(workspaceId, request.Target.WorkspaceId);
    }

    [Fact]
    public void For_RejectsAnEmptyUserId()
    {
        Assert.Throws<ArgumentException>(() =>
            AuthorizationRequest.For(Guid.Empty, "property.read", AuthorizationTarget.Platform));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void For_RejectsABlankPermissionCode(string permissionCode)
    {
        Assert.Throws<ArgumentException>(() =>
            AuthorizationRequest.For(Guid.NewGuid(), permissionCode, AuthorizationTarget.Platform));
    }

    [Fact]
    public void For_RejectsANullPermissionCode()
    {
        Assert.Throws<ArgumentException>(() =>
            AuthorizationRequest.For(Guid.NewGuid(), null!, AuthorizationTarget.Platform));
    }

    [Fact]
    public void For_RejectsANullTarget()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AuthorizationRequest.For(Guid.NewGuid(), "property.read", null!));
    }

    [Fact]
    public void For_RejectsANullTargetBeforeValidatingTheUserId()
    {
        // The null check runs first, so a request that is wrong in both ways reports the null target.
        Assert.Throws<ArgumentNullException>(() =>
            AuthorizationRequest.For(Guid.Empty, "property.read", null!));
    }
}
