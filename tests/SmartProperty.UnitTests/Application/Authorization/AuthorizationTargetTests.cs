using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authorization;

public sealed class AuthorizationTargetTests
{
    [Fact]
    public void Platform_IsPlatformScopedWithoutAWorkspace()
    {
        Assert.Equal(RoleScope.Platform, AuthorizationTarget.Platform.Scope);
        Assert.Null(AuthorizationTarget.Platform.WorkspaceId);
    }

    [Fact]
    public void Workspace_IsWorkspaceScopedAndKeepsTheWorkspaceId()
    {
        var workspaceId = Guid.NewGuid();

        var target = AuthorizationTarget.Workspace(workspaceId);

        Assert.Equal(RoleScope.Workspace, target.Scope);
        Assert.Equal(workspaceId, target.WorkspaceId);
    }

    [Fact]
    public void Workspace_RejectsAnEmptyWorkspaceId()
    {
        Assert.Throws<ArgumentException>(() => AuthorizationTarget.Workspace(Guid.Empty));
    }

    [Fact]
    public void Workspace_TargetsForTheSameWorkspaceAreEqual()
    {
        // Value equality is what lets a caller assert which target a checker was asked about.
        var workspaceId = Guid.NewGuid();

        Assert.Equal(AuthorizationTarget.Workspace(workspaceId), AuthorizationTarget.Workspace(workspaceId));
    }

    [Fact]
    public void Workspace_TargetsForDifferentWorkspacesAreNotEqual()
    {
        Assert.NotEqual(
            AuthorizationTarget.Workspace(Guid.NewGuid()),
            AuthorizationTarget.Workspace(Guid.NewGuid()));
    }

    [Fact]
    public void Platform_IsNotEqualToAWorkspaceTarget()
    {
        Assert.NotEqual(AuthorizationTarget.Platform, AuthorizationTarget.Workspace(Guid.NewGuid()));
    }

    [Fact]
    public void ToString_DoesNotMentionAWorkspaceForThePlatformTarget()
    {
        Assert.Equal("AuthorizationTarget { Platform }", AuthorizationTarget.Platform.ToString());
    }

    [Fact]
    public void ToString_NamesTheWorkspaceForAWorkspaceTarget()
    {
        var workspaceId = Guid.NewGuid();

        Assert.Equal(
            $"AuthorizationTarget {{ Workspace, {workspaceId} }}",
            AuthorizationTarget.Workspace(workspaceId).ToString());
    }
}
