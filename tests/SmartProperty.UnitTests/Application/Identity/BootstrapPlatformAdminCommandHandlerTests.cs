using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Identity;

/// <summary>
/// Exercises the real <see cref="BootstrapPlatformAdminCommandHandler"/>: what a first run provisions, what a
/// second run must not duplicate, and the accounts it refuses to touch.
/// </summary>
/// <remarks>
/// Every configuration value here is test-local. No environment variable, developer setting, or staging account is
/// read, and nothing in this file could reach a real database.
/// </remarks>
public sealed class BootstrapPlatformAdminCommandHandlerTests
{
    private const string AdminEmail = "platform.admin@example.test";
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProvisionedAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakePermissionRepository _permissions = new();
    private readonly FakeRoleRepository _roles = new();
    private readonly FakeDateTimeProvider _clock = new(ProvisionedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePlatformAdminBootstrapCoordinator _bootstrapCoordinator = new();

    [Fact]
    public async Task BlankEmail_IsAConfigurationFailureAndWritesNothing()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand("   "));

        Assert.True(result.IsFailure);
        Assert.Same(BootstrapPlatformAdminErrors.PlatformAdminEmailMissing, result.Error);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _users.GetByEmailCallCount);
    }

    [Fact]
    public async Task UnknownEmail_FailsClearlyAndCreatesNoUser()
    {
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsFailure);
        Assert.Same(BootstrapPlatformAdminErrors.PlatformAdminNotFound, result.Error);

        // The whole point: bootstrap elevates, it never creates an identity or a credential.
        Assert.Null(await _users.GetByEmailAsync(AdminEmail));
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRoles);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task DisabledAccount_IsRefusedRatherThanRevived(UserStatus status)
    {
        var user = SeedUser(status);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsFailure);
        Assert.Same(BootstrapPlatformAdminErrors.PlatformAdminAccountUnavailable, result.Error);
        Assert.Equal(status, user.Status);
        Assert.Empty(_roles.AddedUserAssignments);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task PendingAccount_IsActivated()
    {
        var user = SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(result.Value.UserActivated);
        Assert.Equal(UserStatus.Active, result.Value.UserStatus);
    }

    [Fact]
    public async Task ActiveAccount_IsLeftExactlyAsItWas()
    {
        var user = SeedUser(UserStatus.Active);
        var updatedAtBefore = user.UpdatedAt;
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.UserActivated);
        Assert.Equal(updatedAtBefore, user.UpdatedAt);
    }

    [Fact]
    public async Task AFirstRun_CreatesThePermission()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var permission = Assert.Single(
            _permissions.Added,
            candidate => candidate.Code == PermissionCodes.WorkspaceAccessRequestsReview);
        Assert.Equal(PermissionCodes.WorkspaceAccessRequestsReview, permission.Code);
        Assert.Equal(PermissionCodes.WorkspaceAccessRequestsReviewDescription, permission.Description);
        Assert.True(result.Value.PermissionCreated);
        Assert.Equal(permission.Id, result.Value.PermissionId);
    }

    [Fact]
    public async Task AFirstRun_CreatesAndAttachesThePropertyCreatePermission()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var permission = Assert.Single(
            _permissions.Added,
            candidate => candidate.Code == PermissionCodes.PropertyCreate);
        Assert.Equal(PermissionCodes.PropertyCreateDescription, permission.Description);
        Assert.True(result.Value.PropertyCreatePermissionCreated);
        Assert.Equal(permission.Id, result.Value.PropertyCreatePermissionId);
        Assert.True(result.Value.PropertyCreateRolePermissionCreated);
        Assert.Contains(
            _roles.AddedRolePermissions,
            candidate => candidate.RoleId == result.Value.RoleId && candidate.PermissionId == permission.Id);
    }

    [Fact]
    public async Task AFirstRun_CreatesAndAttachesThePropertyReadPermission()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var permission = Assert.Single(
            _permissions.Added,
            candidate => candidate.Code == PermissionCodes.PropertyRead);
        Assert.Equal(PermissionCodes.PropertyReadDescription, permission.Description);
        Assert.True(result.Value.PropertyReadPermissionCreated);
        Assert.Equal(permission.Id, result.Value.PropertyReadPermissionId);
        Assert.True(result.Value.PropertyReadRolePermissionCreated);
        Assert.Contains(
            _roles.AddedRolePermissions,
            candidate => candidate.RoleId == result.Value.RoleId && candidate.PermissionId == permission.Id);
    }

    [Fact]
    public async Task AFirstRun_ProvisionsEveryDeclaredPlatformAdminPermissionAndNothingElse()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var expectedCodes = new[]
        {
            PermissionCodes.PropertyCreate,
            PermissionCodes.PropertyRead,
            PermissionCodes.WorkspaceAccessRequestsReview
        };
        var actualCodes = _permissions.Added
            .Select(permission => permission.Code)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedCodes, actualCodes);
        Assert.Equal(3, _roles.AddedRolePermissions.Count);
        Assert.All(
            _roles.AddedRolePermissions,
            rolePermission => Assert.Equal(result.Value.RoleId, rolePermission.RoleId));
        Assert.Equal(
            _permissions.Added.Select(permission => permission.Id).Order().ToArray(),
            _roles.AddedRolePermissions.Select(rolePermission => rolePermission.PermissionId).Order().ToArray());
    }

    [Fact]
    public async Task AFirstRun_CreatesThePlatformAdminRoleAtPlatformScope()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var role = Assert.Single(_roles.AddedRoles);
        Assert.Equal(BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName, role.Name);
        Assert.Equal(RoleScope.Platform, role.Scope);
        Assert.Null(role.WorkspaceId);
        Assert.True(result.Value.RoleCreated);
        Assert.Equal(role.Id, result.Value.RoleId);
    }

    [Fact]
    public async Task AFirstRun_AttachesThePermissionToTheRole()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var rolePermission = Assert.Single(
            _roles.AddedRolePermissions,
            candidate => candidate.PermissionId == result.Value.PermissionId);
        Assert.Equal(result.Value.RoleId, rolePermission.RoleId);
        Assert.Equal(result.Value.PermissionId, rolePermission.PermissionId);
        Assert.True(result.Value.RolePermissionCreated);
    }

    [Fact]
    public async Task AFirstRun_AssignsTheRoleToTheConfiguredUser()
    {
        var user = SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var assignment = Assert.Single(_roles.AddedUserAssignments);
        Assert.Equal(user.Id, assignment.UserId);
        Assert.Equal(result.Value.RoleId, assignment.RoleId);
        Assert.True(result.Value.PlatformRoleAssigned);
    }

    [Fact]
    public async Task AFirstRun_CommitsEverythingInOneUnitOfWork()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ASecondRun_ChangesNothing()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var first = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));
        var second = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.MadeNoChange);
        Assert.False(second.Value.UserActivated);
        Assert.False(second.Value.PermissionCreated);
        Assert.False(second.Value.PropertyCreatePermissionCreated);
        Assert.False(second.Value.PropertyReadPermissionCreated);
        Assert.False(second.Value.RoleCreated);
        Assert.False(second.Value.RolePermissionCreated);
        Assert.False(second.Value.PropertyCreateRolePermissionCreated);
        Assert.False(second.Value.PropertyReadRolePermissionCreated);
        Assert.False(second.Value.PlatformRoleAssigned);
    }

    [Fact]
    public async Task ASecondRun_DuplicatesNoRow()
    {
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        var first = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));
        var second = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.Equal(3, _permissions.Added.Count);
        Assert.Single(_roles.AddedRoles);
        Assert.Equal(3, _roles.AddedRolePermissions.Count);
        Assert.Single(_roles.AddedUserAssignments);
        Assert.Equal(3, _roles.RolePermissions.Count);
        Assert.Single(_roles.UserAssignments);

        // And it resolves to the same rows, rather than quietly provisioning a parallel set.
        Assert.Equal(first.Value.PermissionId, second.Value.PermissionId);
        Assert.Equal(first.Value.RoleId, second.Value.RoleId);
    }

    [Fact]
    public async Task AnExistingPermissionAndRole_AreReusedRatherThanRecreated()
    {
        var user = SeedUser(UserStatus.Active);
        var permission = new Permission(
            Guid.NewGuid(),
            PermissionCodes.WorkspaceAccessRequestsReview,
            "Seeded by an earlier deployment.");
        var propertyCreatePermission = new Permission(
            Guid.NewGuid(),
            PermissionCodes.PropertyCreate,
            "Seeded by an earlier deployment.");
        var propertyReadPermission = new Permission(
            Guid.NewGuid(),
            PermissionCodes.PropertyRead,
            "Seeded by an earlier deployment.");
        var role = new Role(
            Guid.NewGuid(),
            BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName,
            RoleScope.Platform,
            workspaceId: null,
            CreatedAt);

        _permissions.Seed(permission);
        _permissions.Seed(propertyCreatePermission);
        _permissions.Seed(propertyReadPermission);
        _roles.Seed(role);
        _roles.Seed(new RolePermission(role.Id, permission.Id));
        _roles.Seed(new RolePermission(role.Id, propertyCreatePermission.Id));
        _roles.Seed(new RolePermission(role.Id, propertyReadPermission.Id));

        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.PermissionCreated);
        Assert.False(result.Value.PropertyCreatePermissionCreated);
        Assert.False(result.Value.PropertyReadPermissionCreated);
        Assert.False(result.Value.RoleCreated);
        Assert.False(result.Value.RolePermissionCreated);
        Assert.False(result.Value.PropertyCreateRolePermissionCreated);
        Assert.False(result.Value.PropertyReadRolePermissionCreated);
        Assert.Equal(permission.Id, result.Value.PermissionId);
        Assert.Equal(propertyCreatePermission.Id, result.Value.PropertyCreatePermissionId);
        Assert.Equal(propertyReadPermission.Id, result.Value.PropertyReadPermissionId);
        Assert.Equal(role.Id, result.Value.RoleId);

        // The role assignment was the only missing piece of an otherwise fully provisioned state.
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_roles.AddedRolePermissions);
        var assignment = Assert.Single(_roles.AddedUserAssignments);
        Assert.Equal(user.Id, assignment.UserId);
        Assert.Equal(role.Id, assignment.RoleId);
        Assert.True(result.Value.PlatformRoleAssigned);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AnExistingPropertyReadPermission_IsReusedAndOnlyAttached()
    {
        var user = SeedUser(UserStatus.Active);
        var reviewPermission = SeedPermission(PermissionCodes.WorkspaceAccessRequestsReview);
        var propertyCreatePermission = SeedPermission(PermissionCodes.PropertyCreate);
        var propertyReadPermission = SeedPermission(PermissionCodes.PropertyRead);
        var role = SeedPlatformAdminRole();
        _roles.Seed(new RolePermission(role.Id, reviewPermission.Id));
        _roles.Seed(new RolePermission(role.Id, propertyCreatePermission.Id));
        _roles.Seed(new UserPlatformRole(user.Id, role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.PropertyReadPermissionCreated);
        Assert.Equal(propertyReadPermission.Id, result.Value.PropertyReadPermissionId);
        Assert.True(result.Value.PropertyReadRolePermissionCreated);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRoles);
        var attachment = Assert.Single(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, attachment.RoleId);
        Assert.Equal(propertyReadPermission.Id, attachment.PermissionId);
        Assert.Empty(_roles.AddedUserAssignments);
    }

    [Fact]
    public async Task AnEnvironmentBootstrappedBeforePropertyRead_GainsOnlyPropertyReadOnTheNextRun()
    {
        var user = SeedUser(UserStatus.Active);
        var reviewPermission = SeedPermission(PermissionCodes.WorkspaceAccessRequestsReview);
        var propertyCreatePermission = SeedPermission(PermissionCodes.PropertyCreate);
        var role = SeedPlatformAdminRole();
        _roles.Seed(new RolePermission(role.Id, reviewPermission.Id));
        _roles.Seed(new RolePermission(role.Id, propertyCreatePermission.Id));
        _roles.Seed(new UserPlatformRole(user.Id, role.Id));
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.PropertyReadPermissionCreated);
        Assert.True(result.Value.PropertyReadRolePermissionCreated);
        var permission = Assert.Single(_permissions.Added);
        Assert.Equal(PermissionCodes.PropertyRead, permission.Code);
        var attachment = Assert.Single(_roles.AddedRolePermissions);
        Assert.Equal(role.Id, attachment.RoleId);
        Assert.Equal(permission.Id, attachment.PermissionId);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_roles.AddedUserAssignments);
    }

    [Fact]
    public void ThePropertyReadPermissionCodeIsTheDeclaredContract()
    {
        Assert.Equal("property.read", PermissionCodes.PropertyRead);
        Assert.Equal("Read canonical properties.", PermissionCodes.PropertyReadDescription);
    }

    [Fact]
    public async Task TheConfiguredEmailIsNormalizedTheSameWayAStoredEmailIs()
    {
        var user = SeedUser(UserStatus.Active);
        var handler = CreateHandler();

        var result = await handler.Handle(new BootstrapPlatformAdminCommand($"  {AdminEmail.ToUpperInvariant()} "));

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value.UserId);
    }

    [Fact]
    public void TheCommandDoesNotEchoTheConfiguredEmail()
    {
        var command = new BootstrapPlatformAdminCommand(AdminEmail);

        Assert.DoesNotContain(AdminEmail, command.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private User SeedUser(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), AdminEmail, "Platform", "Admin", CreatedAt);

        switch (status)
        {
            case UserStatus.Active:
                user.Activate(CreatedAt);
                break;
            case UserStatus.Suspended:
                user.Suspend(CreatedAt);
                break;
            case UserStatus.Deactivated:
                user.Deactivate(CreatedAt);
                break;
            case UserStatus.Pending:
            default:
                break;
        }

        _users.Seed(user);

        return user;
    }

    private Permission SeedPermission(string code)
    {
        var permission = new Permission(Guid.NewGuid(), code, "Seeded by an earlier deployment.");
        _permissions.Seed(permission);
        return permission;
    }

    private Role SeedPlatformAdminRole()
    {
        var role = new Role(
            Guid.NewGuid(),
            BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName,
            RoleScope.Platform,
            workspaceId: null,
            CreatedAt);
        _roles.Seed(role);
        return role;
    }

    private BootstrapPlatformAdminCommandHandler CreateHandler()
    {
        return new BootstrapPlatformAdminCommandHandler(
            _users,
            _permissions,
            _roles,
            _clock,
            _unitOfWork,
            _bootstrapCoordinator);
    }
}
