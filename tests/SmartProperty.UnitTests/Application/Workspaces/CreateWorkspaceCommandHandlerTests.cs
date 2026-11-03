using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Application.Workspaces;
using SmartProperty.Application.Workspaces.Administration.Create;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Workspaces;

public sealed class CreateWorkspaceCommandHandlerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\tName")]
    [InlineData("Name\n")]
    [InlineData("A\0B")]
    public async Task InvalidNamesDoNotSave(string? name)
    {
        var save = new FakeUnitOfWork();
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(DateTimeOffset.UtcNow), save);
        var result = await handler.Handle(new(name));
        Assert.Equal("workspace_admin.validation_failed", result.Error!.Code);
        Assert.Equal(0, save.SaveChangesCallCount);
    }

    [Fact]
    public async Task Utf16BoundAndUnicodePreservation()
    {
        var repository = new FakeWorkspaceRepository();
        var save = new FakeUnitOfWork();
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var handler = Handler(repository, new FakeDateTimeProvider(now), save);
        var name = string.Concat(Enumerable.Repeat("😀", 100));
        var valid = await handler.Handle(new("  " + name + "  "));
        Assert.True(valid.IsSuccess);
        Assert.Equal(name, valid.Value.Name);
        Assert.Equal(now, valid.Value.CreatedAt);
        Assert.Equal(now, valid.Value.UpdatedAt);
        Assert.NotEqual(Guid.Empty, valid.Value.Id);
        Assert.NotNull(await repository.GetByIdAsync(valid.Value.Id));
        Assert.True((await handler.Handle(new(name + "x"))).IsFailure);
        var unicode = await handler.Handle(new("  مصر  AbC  "));
        Assert.Equal("مصر  AbC", unicode.Value.Name);
        Assert.Equal(2, save.SaveChangesCallCount);
    }

    [Fact]
    public async Task SaveFailurePropagatesWithoutRetry()
    {
        var save = new FakeUnitOfWork { ExceptionToThrow = new InvalidOperationException("test failure") };
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(DateTimeOffset.UtcNow), save);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new("Name")));
        Assert.Equal(1, save.SaveChangesCallCount);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");

    private static CreateWorkspaceAdmin ValidAdmin(string email = "Admin@Example.com") =>
        new(email, "Str0ng-pass", "Sara", "Admin");

    private static CreateWorkspaceCommandHandler Handler(
        FakeWorkspaceRepository workspaces,
        FakeDateTimeProvider clock,
        FakeUnitOfWork save,
        FakeUserRepository? users = null,
        FakeUserCredentialRepository? credentials = null,
        FakeWorkspaceMembershipRepository? memberships = null,
        FakeRoleRepository? roles = null,
        FakePermissionRepository? permissions = null,
        FakePasswordHasher? hasher = null) =>
        new(workspaces, users ?? new(), credentials ?? new(), memberships ?? new(), roles ?? new(),
            permissions ?? new(), hasher ?? new(), clock, save);

    [Fact]
    public async Task WithoutAdminNoAccountRoleOrMembershipIsCreated()
    {
        var users = new FakeUserRepository();
        var roles = new FakeRoleRepository();
        var memberships = new FakeWorkspaceMembershipRepository();
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(Now), new FakeUnitOfWork(),
            users: users, roles: roles, memberships: memberships);
        var result = await handler.Handle(new("Name"));
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.AdminUserId);
        Assert.Empty(roles.AddedRoles);
        Assert.Empty(memberships.Added);
    }

    [Fact]
    public async Task AdminCreatesActiveAccountWorkspaceAdminRoleAndMembershipInOneSave()
    {
        var workspaces = new FakeWorkspaceRepository();
        var users = new FakeUserRepository();
        var roles = new FakeRoleRepository();
        var memberships = new FakeWorkspaceMembershipRepository();
        var permissions = new FakePermissionRepository();
        var hasher = new FakePasswordHasher();
        var save = new FakeUnitOfWork();
        var handler = Handler(workspaces, new FakeDateTimeProvider(Now), save, users, null, memberships, roles, permissions, hasher);

        var result = await handler.Handle(new("Name", ValidAdmin()));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, save.SaveChangesCallCount);
        Assert.Equal(1, hasher.HashCallCount);
        var userId = result.Value.AdminUserId!.Value;
        var user = (await users.GetByIdAsync(userId))!;
        Assert.Equal("admin@example.com", user.Email);
        Assert.Equal(UserStatus.Active, user.Status);
        var role = Assert.Single(roles.AddedRoles);
        Assert.Equal(WorkspaceRoles.AdminRoleName, role.Name);
        Assert.Equal(RoleScope.Workspace, role.Scope);
        Assert.Equal(result.Value.Id, role.WorkspaceId);
        var permission = Assert.Single(permissions.Added);
        Assert.Equal(PermissionCodes.WorkspaceRead, permission.Code);
        Assert.Equal(permission.Id, Assert.Single(roles.AddedRolePermissions).PermissionId);
        var membership = Assert.Single(memberships.Added);
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(result.Value.Id, membership.WorkspaceId);
        var assignment = Assert.Single(roles.AddedMembershipAssignments);
        Assert.Equal(membership.Id, assignment.WorkspaceMembershipId);
        Assert.Equal(role.Id, assignment.RoleId);
    }

    [Fact]
    public async Task AdminReusesExistingWorkspaceReadPermission()
    {
        var permissions = new FakePermissionRepository();
        var existing = new Permission(Guid.NewGuid(), PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceReadDescription);
        permissions.Seed(existing);
        var roles = new FakeRoleRepository();
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(Now), new FakeUnitOfWork(),
            roles: roles, permissions: permissions);
        Assert.True((await handler.Handle(new("Name", ValidAdmin()))).IsSuccess);
        Assert.Empty(permissions.Added);
        Assert.Equal(existing.Id, Assert.Single(roles.AddedRolePermissions).PermissionId);
    }

    [Fact]
    public async Task ExistingEmailIsRejectedBeforeAnythingIsSaved()
    {
        var users = new FakeUserRepository();
        users.Seed(new User(Guid.NewGuid(), "admin@example.com", "A", "B", Now));
        var save = new FakeUnitOfWork();
        var roles = new FakeRoleRepository();
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(Now), save, users, roles: roles);
        var result = await handler.Handle(new("Name", ValidAdmin("  ADMIN@example.com ")));
        Assert.Equal("users.email_already_exists", result.Error!.Code);
        Assert.Equal(0, save.SaveChangesCallCount);
        Assert.Empty(roles.AddedRoles);
    }

    [Theory]
    [InlineData(null, "Str0ng-pass", "A", "B")]
    [InlineData("not-an-email", "Str0ng-pass", "A", "B")]
    [InlineData("a@b.com", "", "A", "B")]
    [InlineData("a@b.com", "Str0ng-pass", "", "B")]
    [InlineData("a@b.com", "Str0ng-pass", "A", null)]
    public async Task InvalidAdminDoesNotSave(string? email, string? password, string? first, string? last)
    {
        var save = new FakeUnitOfWork();
        var hasher = new FakePasswordHasher();
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(Now), save, hasher: hasher);
        var result = await handler.Handle(new("Name", new(email, password, first, last)));
        Assert.Equal("workspace_admin.validation_failed", result.Error!.Code);
        Assert.Equal(0, save.SaveChangesCallCount);
        Assert.Equal(0, hasher.HashCallCount);
    }

    [Fact]
    public async Task EmailRaceOnSaveReturnsEmailAlreadyExists()
    {
        var save = new FakeUnitOfWork { ExceptionToThrow = FakeUnitOfWork.UserEmailUniqueConstraintViolation() };
        var handler = Handler(new FakeWorkspaceRepository(), new FakeDateTimeProvider(Now), save);
        var result = await handler.Handle(new("Name", ValidAdmin()));
        Assert.Equal("users.email_already_exists", result.Error!.Code);
    }
}
