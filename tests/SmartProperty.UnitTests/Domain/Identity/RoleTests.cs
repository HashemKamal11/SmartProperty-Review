using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Identity;

public sealed class RoleTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlatformRole_HasPlatformScopeAndNoWorkspace()
    {
        var role = new Role(Guid.NewGuid(), "Platform Administrator", RoleScope.Platform, null, CreatedAt);

        Assert.Equal(RoleScope.Platform, role.Scope);
        Assert.Null(role.WorkspaceId);
    }

    [Fact]
    public void WorkspaceRole_KeepsItsWorkspace()
    {
        var workspaceId = Guid.NewGuid();

        var role = new Role(Guid.NewGuid(), "Workspace Owner", RoleScope.Workspace, workspaceId, CreatedAt);

        Assert.Equal(RoleScope.Workspace, role.Scope);
        Assert.Equal(workspaceId, role.WorkspaceId);
    }

    [Fact]
    public void Constructor_RejectsPlatformRoleWithWorkspace()
    {
        Assert.Throws<ArgumentException>(() =>
            new Role(Guid.NewGuid(), "Platform Administrator", RoleScope.Platform, Guid.NewGuid(), CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsWorkspaceRoleWithoutWorkspace()
    {
        Assert.Throws<ArgumentException>(() =>
            new Role(Guid.NewGuid(), "Workspace Owner", RoleScope.Workspace, null, CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsWorkspaceRoleWithEmptyWorkspaceId()
    {
        Assert.Throws<ArgumentException>(() =>
            new Role(Guid.NewGuid(), "Workspace Owner", RoleScope.Workspace, Guid.Empty, CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsUndefinedScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Role(Guid.NewGuid(), "Mystery", (RoleScope)99, null, CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(() =>
            new Role(Guid.Empty, "Platform Administrator", RoleScope.Platform, null, CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankName(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new Role(Guid.NewGuid(), name, RoleScope.Platform, null, CreatedAt));
    }

    [Fact]
    public void Constructor_TrimsNameAndSetsBothTimestamps()
    {
        var role = new Role(Guid.NewGuid(), "  Platform Administrator  ", RoleScope.Platform, null, CreatedAt);

        Assert.Equal("Platform Administrator", role.Name);
        Assert.Equal(CreatedAt, role.CreatedAt);
        Assert.Equal(CreatedAt, role.UpdatedAt);
    }

    [Fact]
    public void Rename_UpdatesNameAndTimestamp()
    {
        var role = new Role(Guid.NewGuid(), "Platform Administrator", RoleScope.Platform, null, CreatedAt);
        var renamedAt = CreatedAt.AddHours(1);

        role.Rename("  Platform Owner  ", renamedAt);

        Assert.Equal("Platform Owner", role.Name);
        Assert.Equal(renamedAt, role.UpdatedAt);
        Assert.Equal(CreatedAt, role.CreatedAt);
    }

    [Fact]
    public void Rename_RejectsTimestampBeforeTheCurrentOne()
    {
        var role = new Role(Guid.NewGuid(), "Platform Administrator", RoleScope.Platform, null, CreatedAt);

        Assert.Throws<ArgumentException>(() => role.Rename("Platform Owner", CreatedAt.AddSeconds(-1)));
    }
}
