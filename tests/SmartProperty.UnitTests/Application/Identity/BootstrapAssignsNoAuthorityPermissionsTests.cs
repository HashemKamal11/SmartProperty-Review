using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Identity;

/// <summary>
/// Proves the real platform administrator bootstrap neither creates nor assigns any property authority permission,
/// in every state it can meet, and that holding the Platform Admin role never confers one.
/// </summary>
/// <remarks>
/// The authority codes are written out as literals rather than through <see cref="PermissionCodes"/>, so renaming a
/// constant cannot quietly weaken a test. Everything is in memory and no bootstrap is run against a database.
/// </remarks>
public sealed class BootstrapAssignsNoAuthorityPermissionsTests
{
    private const string AdminEmail = "platform.admin@example.test";

    private static readonly string[] AuthorityCodes =
    [
        "marketplace.authority.legal_reviews.record",
        "marketplace.authority.grants.issue",
        "marketplace.authority.grants.revoke",
        "marketplace.authority.grants.read",
    ];

    // The seven permissions bootstrap provisions and attaches today, in ordinal order.
    private static readonly string[] ProvisionedCodes =
    [
        "marketplace.submissions.review.decide",
        "marketplace.submissions.review.read",
        "property.archive",
        "property.create",
        "property.read",
        "property.update",
        "workspace.access_requests.review",
    ];

    private static readonly string[] OriginalFiveCodes =
    [
        "property.archive",
        "property.create",
        "property.read",
        "property.update",
        "workspace.access_requests.review",
    ];

    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProvisionedAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakePermissionRepository _permissions = new();
    private readonly FakeRoleRepository _roles = new();
    private readonly FakeDateTimeProvider _clock = new(ProvisionedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePlatformAdminBootstrapCoordinator _coordinator = new();

    [Fact]
    public async Task AFirstRunCreatesAndAttachesExactlyTheSevenAndNoAuthorityPermission()
    {
        var user = SeedUser(UserStatus.Pending);

        var result = await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.IsSuccess);
        Assert.Equal(ProvisionedCodes, _permissions.Added.Select(permission => permission.Code).Order(StringComparer.Ordinal));
        Assert.Equal(7, _roles.AddedRolePermissions.Count);
        AssertNoAuthorityRows();
        Assert.Equal(ProvisionedCodes, await EffectiveCodesAsync(user.Id));
    }

    [Fact]
    public async Task AReplayChangesNothingAndStillCreatesNoAuthorityPermission()
    {
        var user = SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));
        var second = await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(second.Value.MadeNoChange);
        Assert.Equal(7, _permissions.Added.Count);
        Assert.Equal(7, _roles.AddedRolePermissions.Count);
        Assert.Equal(7, _roles.RolePermissions.Count);
        AssertNoAuthorityRows();
        Assert.Equal(ProvisionedCodes, await EffectiveCodesAsync(user.Id));
    }

    [Fact]
    public async Task AFullyBootstrappedStateIsLeftExactlyAsItWasWithNoAuthorityPermission()
    {
        var user = SeedUser(UserStatus.Active);
        var role = SeedPlatformAdminRole();
        foreach (var code in ProvisionedCodes)
        {
            _roles.Seed(new RolePermission(role.Id, SeedPermission(code).Id));
        }

        _roles.Seed(new UserPlatformRole(user.Id, role.Id));

        var result = await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.Value.MadeNoChange);
        Assert.Empty(_permissions.Added);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Empty(_roles.AddedRoles);
        Assert.Empty(_roles.AddedUserAssignments);
        AssertNoAuthorityRows();
        Assert.Equal(ProvisionedCodes, await EffectiveCodesAsync(user.Id));
    }

    [Fact]
    public async Task AnEnvironmentBootstrappedWithTheOriginalFiveGainsOnlyTheTwoListingReviewPermissions()
    {
        var user = SeedUser(UserStatus.Active);
        var role = SeedPlatformAdminRole();
        foreach (var code in OriginalFiveCodes)
        {
            _roles.Seed(new RolePermission(role.Id, SeedPermission(code).Id));
        }

        _roles.Seed(new UserPlatformRole(user.Id, role.Id));

        await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.Equal(
            new[] { "marketplace.submissions.review.decide", "marketplace.submissions.review.read" },
            _permissions.Added.Select(permission => permission.Code).Order(StringComparer.Ordinal));
        Assert.Equal(2, _roles.AddedRolePermissions.Count);
        AssertNoAuthorityRows();
        Assert.Equal(ProvisionedCodes, await EffectiveCodesAsync(user.Id));
    }

    [Fact]
    public async Task AuthorityDefinitionsThatAlreadyExistButAreUnassignedAreNeverAttachedByBootstrap()
    {
        var user = SeedUser(UserStatus.Active);
        var role = SeedPlatformAdminRole();
        foreach (var code in ProvisionedCodes)
        {
            _roles.Seed(new RolePermission(role.Id, SeedPermission(code).Id));
        }

        _roles.Seed(new UserPlatformRole(user.Id, role.Id));
        foreach (var code in AuthorityCodes)
        {
            SeedPermission(code);
        }

        var result = await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.True(result.Value.MadeNoChange);
        Assert.Empty(_roles.AddedRolePermissions);
        Assert.Equal(7, _roles.RolePermissions.Count);
        Assert.Equal(ProvisionedCodes, await EffectiveCodesAsync(user.Id));
    }

    [Fact]
    public async Task HoldingThePlatformAdminRoleConfersNoAuthorityPermissionEvenWhenSomeoneElseWasAssignedOne()
    {
        var admin = SeedUser(UserStatus.Active);
        var adminRole = SeedPlatformAdminRole();
        foreach (var code in ProvisionedCodes)
        {
            _roles.Seed(new RolePermission(adminRole.Id, SeedPermission(code).Id));
        }

        _roles.Seed(new UserPlatformRole(admin.Id, adminRole.Id));

        // A different platform role, explicitly assigned one authority permission to a different user.
        var reviewerRole = new Role(Guid.NewGuid(), "Authority Legal Reviewer", RoleScope.Platform, workspaceId: null, CreatedAt);
        var reviewer = new User(Guid.NewGuid(), "legal.reviewer@example.test", "Legal", "Reviewer", CreatedAt);
        reviewer.Activate(CreatedAt);
        _users.Seed(reviewer);
        _roles.Seed(reviewerRole);
        _roles.Seed(new RolePermission(reviewerRole.Id, SeedPermission("marketplace.authority.legal_reviews.record").Id));
        _roles.Seed(new UserPlatformRole(reviewer.Id, reviewerRole.Id));

        await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        // The admin has the seven and no authority permission. The explicitly assigned reviewer has exactly one.
        Assert.Equal(ProvisionedCodes, await EffectiveCodesAsync(admin.Id));
        Assert.Equal(new[] { "marketplace.authority.legal_reviews.record" }, await EffectiveCodesAsync(reviewer.Id));
        Assert.Empty(_roles.AddedRolePermissions);
    }

    [Fact]
    public async Task NoAuthorityPermissionIsDeniedByDefaultToAUserWithNoExplicitAssignment()
    {
        var user = SeedUser(UserStatus.Pending);

        await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var effective = await EffectiveCodesAsync(user.Id);
        foreach (var code in AuthorityCodes)
        {
            Assert.DoesNotContain(code, effective);
        }
    }

    [Fact]
    public async Task BootstrapNeverTouchesAWorkspaceRoleOrWorkspaceRead()
    {
        var user = SeedUser(UserStatus.Pending);

        await CreateHandler().Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        Assert.All(_roles.AddedRoles, role =>
        {
            Assert.Equal(RoleScope.Platform, role.Scope);
            Assert.Null(role.WorkspaceId);
        });
        Assert.Empty(_roles.AddedMembershipAssignments);
        Assert.DoesNotContain(_permissions.Added, permission => permission.Code == "workspace.read");
        Assert.DoesNotContain("workspace.read", await EffectiveCodesAsync(user.Id));
    }

    [Fact]
    public async Task EveryBootstrapStateLeavesTheAuthorityCodesAbsentFromAllRolePermissions()
    {
        // Fresh, upgraded and replayed states share one assertion: no role-permission row references an authority code.
        SeedUser(UserStatus.Pending);
        var handler = CreateHandler();

        await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));
        await handler.Handle(new BootstrapPlatformAdminCommand(AdminEmail));

        var attachedCodes = new List<string>();
        foreach (var rolePermission in _roles.RolePermissions)
        {
            attachedCodes.Add((await _permissions.GetByIdAsync(rolePermission.PermissionId))!.Code);
        }

        Assert.Equal(7, attachedCodes.Count);
        Assert.Empty(attachedCodes.Where(code => AuthorityCodes.Contains(code, StringComparer.Ordinal)).ToArray());
    }

    // ----- helpers

    private void AssertNoAuthorityRows()
    {
        foreach (var code in AuthorityCodes)
        {
            Assert.DoesNotContain(_permissions.Added, permission => permission.Code == code);
        }
    }

    // What the user actually holds: the user's platform roles, their role permissions, and those permissions' codes.
    private async Task<string[]> EffectiveCodesAsync(Guid userId)
    {
        var roleIds = _roles.UserAssignments.Where(assignment => assignment.UserId == userId).Select(assignment => assignment.RoleId).ToHashSet();
        var codes = new List<string>();

        foreach (var rolePermission in _roles.RolePermissions.Where(candidate => roleIds.Contains(candidate.RoleId)))
        {
            codes.Add((await _permissions.GetByIdAsync(rolePermission.PermissionId))!.Code);
        }

        return codes.Order(StringComparer.Ordinal).ToArray();
    }

    private User SeedUser(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), AdminEmail, "Platform", "Admin", CreatedAt);

        if (status == UserStatus.Active)
        {
            user.Activate(CreatedAt);
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
        return new BootstrapPlatformAdminCommandHandler(_users, _permissions, _roles, _clock, _unitOfWork, _coordinator);
    }
}
