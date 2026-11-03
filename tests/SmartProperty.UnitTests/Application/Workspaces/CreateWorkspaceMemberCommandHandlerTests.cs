using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Workspaces;
using SmartProperty.Application.Workspaces.Members.Create;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Workspaces;

public sealed class CreateWorkspaceMemberCommandHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeUserCredentialRepository _credentials = new();
    private readonly FakeWorkspaceMembershipRepository _memberships = new();
    private readonly FakeRoleRepository _roles = new();
    private readonly FakePermissionRepository _permissions = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeUnitOfWork _save = new();
    private readonly FakeAccessContextReader _access = new();
    private readonly Workspace _workspace = new(Guid.NewGuid(), "Acme", Now);
    private readonly Guid _adminId = Guid.NewGuid();

    // By default the caller administers _workspace: workspace.members.create is held there.
    private void CallerAdministers(params Workspace[] administered) =>
        _access.Context = new([], [], administered
            .Select(workspace => new WorkspaceAccessContext(
                workspace.Id, workspace.Name, ["Workspace Admin"], [PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceMembersCreate]))
            .ToArray());

    private CreateWorkspaceMemberCommandHandler Handler()
    {
        _workspaces.AddAsync(_workspace);
        if (_access.Context.Workspaces.Count == 0 && !_noAdministeredWorkspace)
            CallerAdministers(_workspace);
        return new(_access, _workspaces, _users, _credentials, _memberships, _roles, _permissions, _hasher,
            new FakeDateTimeProvider(Now), _save);
    }

    private bool _noAdministeredWorkspace;

    private CreateWorkspaceMemberCommand Command(string? email = "Member@Example.com") =>
        new(_adminId, email, "Str0ng-pass", "Mona", "Member");

    [Fact]
    public async Task CreatesAnActiveMemberWithTheDefaultMemberRoleInOneSave()
    {
        var result = await Handler().Handle(Command());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _save.SaveChangesCallCount);
        Assert.Equal(1, _hasher.HashCallCount);
        var user = (await _users.GetByIdAsync(result.Value.UserId))!;
        Assert.Equal("member@example.com", user.Email);
        Assert.Equal(UserStatus.Active, user.Status);
        var role = Assert.Single(_roles.AddedRoles);
        Assert.Equal(WorkspaceRoles.MemberRoleName, role.Name);
        Assert.Equal(_workspace.Id, role.WorkspaceId);
        Assert.Equal(PermissionCodes.WorkspaceRead, Assert.Single(_permissions.Added).Code);
        Assert.Equal(_permissions.Added[0].Id, Assert.Single(_roles.AddedRolePermissions).PermissionId);
        var membership = Assert.Single(_memberships.Added);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(_workspace.Id, membership.WorkspaceId);
        Assert.Equal(membership.Id, Assert.Single(_roles.AddedMembershipAssignments).WorkspaceMembershipId);
        Assert.Equal("Member", result.Value.RoleName);
    }

    [Fact]
    public async Task AnExistingExactMemberRoleIsReusedNotDuplicated()
    {
        var permission = new Permission(Guid.NewGuid(), PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceReadDescription);
        _permissions.Seed(permission);
        var role = new Role(Guid.NewGuid(), WorkspaceRoles.MemberRoleName, RoleScope.Workspace, _workspace.Id, Now);
        _roles.Seed(role);
        _roles.Seed(new RolePermission(role.Id, permission.Id));

        var result = await Handler().Handle(Command());

        Assert.True(result.IsSuccess);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, Assert.Single(_roles.AddedMembershipAssignments).RoleId);
    }

    [Fact]
    public async Task AMemberRoleCarryingMoreThanWorkspaceReadFailsClosedAndSavesNothing()
    {
        var read = new Permission(Guid.NewGuid(), PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceReadDescription);
        var extra = new Permission(Guid.NewGuid(), PermissionCodes.PropertyCreate, PermissionCodes.PropertyCreateDescription);
        _permissions.Seed(read);
        _permissions.Seed(extra);
        var role = new Role(Guid.NewGuid(), WorkspaceRoles.MemberRoleName, RoleScope.Workspace, _workspace.Id, Now);
        _roles.Seed(role);
        _roles.Seed(new RolePermission(role.Id, read.Id));
        _roles.Seed(new RolePermission(role.Id, extra.Id));

        var result = await Handler().Handle(Command());

        Assert.Equal("workspace_members.member_role_misconfigured", result.Error!.Code);
        Assert.Equal(0, _save.SaveChangesCallCount);
        Assert.Empty(_memberships.Added);
    }

    [Fact]
    public async Task ACallerWhoAdministersNoWorkspaceIsForbiddenAndNothingIsSaved()
    {
        _noAdministeredWorkspace = true;

        var result = await Handler().Handle(Command());

        Assert.Equal("workspace_members.forbidden", result.Error!.Code);
        Assert.Equal(SmartProperty.Common.Results.ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(0, _save.SaveChangesCallCount);
        Assert.Equal(0, _hasher.HashCallCount);
    }

    [Fact]
    public async Task AWorkspaceReadOnlyMemberIsForbidden()
    {
        _access.Context = new([], [], [new WorkspaceAccessContext(_workspace.Id, "Acme", ["Member"], [PermissionCodes.WorkspaceRead])]);

        var result = await Handler().Handle(Command());

        Assert.Equal("workspace_members.forbidden", result.Error!.Code);
        Assert.Equal(0, _save.SaveChangesCallCount);
    }

    [Fact]
    public async Task TheMemberJoinsTheWorkspaceTheCallerAdministersAndTheCallerIdIsWhatIsRead()
    {
        var result = await Handler().Handle(Command());

        Assert.Equal(_workspace.Id, result.Value.WorkspaceId);
        Assert.Equal(_adminId, _access.LastUserId);
    }

    [Fact]
    public async Task ACallerAdministeringSeveralWorkspacesMustBeDisambiguated()
    {
        CallerAdministers(_workspace, new Workspace(Guid.NewGuid(), "Other", Now));

        var result = await Handler().Handle(Command());

        Assert.Equal("workspace_members.ambiguous_workspace", result.Error!.Code);
        Assert.Equal(0, _save.SaveChangesCallCount);
    }

    [Fact]
    public async Task AnAdministeredWorkspaceThatNoLongerExistsIsNotFound()
    {
        CallerAdministers(new Workspace(Guid.NewGuid(), "Gone", Now));

        var result = await Handler().Handle(Command());

        Assert.Equal("workspace_members.workspace_not_found", result.Error!.Code);
        Assert.Equal(0, _save.SaveChangesCallCount);
    }

    [Fact]
    public async Task AnExistingEmailIsRejectedAndNothingIsSaved()
    {
        _users.Seed(new User(Guid.NewGuid(), "member@example.com", "A", "B", Now));

        var result = await Handler().Handle(Command("  MEMBER@example.com "));

        Assert.Equal("users.email_already_exists", result.Error!.Code);
        Assert.Equal(0, _save.SaveChangesCallCount);
        Assert.Empty(_roles.AddedRoles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-email")]
    public async Task InvalidAccountDetailsAreRejectedBeforeAnyWork(string? email)
    {
        var result = await Handler().Handle(Command(email));

        Assert.Equal("workspace_members.validation_failed", result.Error!.Code);
        Assert.Equal(0, _hasher.HashCallCount);
        Assert.Equal(0, _save.SaveChangesCallCount);
    }

    [Fact]
    public async Task AnEmailRaceOnSaveIsReportedAsAnExistingEmail()
    {
        _save.ExceptionToThrow = FakeUnitOfWork.UserEmailUniqueConstraintViolation();

        var result = await Handler().Handle(Command());

        Assert.Equal("users.email_already_exists", result.Error!.Code);
    }

    [Fact]
    public async Task ARoleRaceOnSaveAsksTheCallerToRetry()
    {
        _save.ExceptionToThrow = FakeUnitOfWork.WorkspaceRoleNameUniqueConstraintViolation();

        var result = await Handler().Handle(Command());

        Assert.Equal("workspace_members.concurrent_provisioning", result.Error!.Code);
    }
}
