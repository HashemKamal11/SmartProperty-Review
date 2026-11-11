using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Api.Infrastructure.Development;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Development;

/// <summary>
/// The Development provisioning commands against real PostgreSQL: the administrator password reset touches one account,
/// the Development roles hold exactly the intended permissions, grants are recorded for real, distinct, independent
/// actors, and the reviewer relationship check goes beyond comparing ids.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class DevelopmentProvisioningTests(ApiPostgreSqlFixture fixture)
{
    private const string TestOnlyPassword = "test-only-Passw0rd!";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly PasswordHasher _hasher = new();

    // ---- the administrator password

    [Fact]
    public async Task ResettingTheAdministratorPasswordChangesThatCredentialAndNoOtherAndRevokesItsSessions()
    {
        await using var context = fixture.CreateContext();
        var admin = await EnsureAdministratorAsync(context);
        var bystander = await AddUserAsync(context, UniqueEmail("bystander"));
        context.RefreshTokens.Add(new RefreshToken(Guid.NewGuid(), admin.Id, Guid.NewGuid().ToString("N"), Now, Now.AddDays(1)));
        await context.SaveChangesAsync();
        var bystanderHashBefore = await HashOfAsync(context, bystander.Id);

        await new DevelopmentAdminPassword(context, _hasher).ResetAsync("A-brand-new-Passw0rd", Now.AddMinutes(1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var adminHash = await HashOfAsync(verify, admin.Id);
        Assert.Equal(SmartProperty.Application.Abstractions.Authentication.PasswordVerificationStatus.Success, _hasher.Verify("A-brand-new-Passw0rd", adminHash));
        Assert.Equal(SmartProperty.Application.Abstractions.Authentication.PasswordVerificationStatus.Failed, _hasher.Verify(TestOnlyPassword, adminHash));
        Assert.Equal(bystanderHashBefore, await HashOfAsync(verify, bystander.Id));
        Assert.Empty(await verify.RefreshTokens.Where(token => token.UserId == admin.Id && token.RevokedAt == null).ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task APasswordThatIsTooShortIsRefusedAndNothingChanges(string password)
    {
        await using var context = fixture.CreateContext();
        var admin = await EnsureAdministratorAsync(context);
        var before = await HashOfAsync(context, admin.Id);

        await Assert.ThrowsAsync<ProvisioningRefusedException>(
            () => new DevelopmentAdminPassword(context, _hasher).ResetAsync(password, Now, CancellationToken.None));

        Assert.Equal(before, await HashOfAsync(fixture.CreateContext(), admin.Id));
    }

    // ---- roles and the legal reviewer

    [Fact]
    public async Task TheDevelopmentRolesHoldExactlyTheIntendedPermissions()
    {
        await using var context = fixture.CreateContext();
        var admin = await EnsureAdministratorAsync(context);

        await new DevelopmentActorProvisioner(context, _hasher).ProvisionAsync(TestOnlyPassword, Now, CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var reviewer = await verify.Users.SingleAsync(user => user.Email == DevelopmentActorProvisioner.LegalReviewerEmail);
        Assert.Equal(UserStatus.Active, reviewer.Status);
        Assert.True(await PersistedAccess.HoldsPlatformPermissionAsync(verify, admin.Id, PermissionCodes.MarketplaceSettingsManage, default));
        Assert.True(await PersistedAccess.HoldsPlatformPermissionAsync(verify, admin.Id, PermissionCodes.MarketplaceAuthorityGrantsIssue, default));
        Assert.False(await PersistedAccess.HoldsPlatformPermissionAsync(verify, admin.Id, PermissionCodes.MarketplaceAuthorityLegalReviewsRecord, default));

        // The legal reviewer holds one permission and nothing else: no property, review, owner-verification or admin power.
        Assert.Equal([PermissionCodes.MarketplaceAuthorityLegalReviewsRecord], await PermissionCodesOfAsync(verify, reviewer.Id));
        Assert.False(await PersistedAccess.HoldsPlatformPermissionAsync(verify, reviewer.Id, PermissionCodes.MarketplaceAuthorityGrantsIssue, default));
        Assert.False(await PersistedAccess.HoldsPlatformPermissionAsync(verify, reviewer.Id, PermissionCodes.MarketplaceSubmissionsReviewDecide, default));
        Assert.False(await PersistedAccess.HoldsPlatformPermissionAsync(verify, reviewer.Id, PermissionCodes.PropertyCreate, default));
    }

    [Fact]
    public async Task ProvisioningAgainChangesNothingAndLeavesTheLegalReviewersPasswordAlone()
    {
        await using var context = fixture.CreateContext();
        await EnsureProvisionedAsync(context);
        var before = await HashOfAsync(context, (await context.Users.SingleAsync(u => u.Email == DevelopmentActorProvisioner.LegalReviewerEmail)).Id);

        var report = await new DevelopmentActorProvisioner(context, _hasher).ProvisionAsync(null, Now.AddMinutes(1), CancellationToken.None);

        Assert.DoesNotContain(report.Lines, line => line.StartsWith("Created", StringComparison.Ordinal) || line.Contains("now holds", StringComparison.Ordinal));
        Assert.Equal(before, await HashOfAsync(fixture.CreateContext(), (await context.Users.SingleAsync(u => u.Email == DevelopmentActorProvisioner.LegalReviewerEmail)).Id));
    }

    [Fact]
    public async Task ADevelopmentRoleThatAlreadyHoldsAnExtraPermissionIsRefusedNotTolerated()
    {
        await using var context = fixture.CreateContext();
        await EnsureProvisionedAsync(context);
        var role = await context.Roles.SingleAsync(r => r.Name == DevelopmentActorProvisioner.LegalReviewerRoleName);
        var extra = await context.Permissions.FirstAsync(p => p.Code == PermissionCodes.MarketplaceSettingsManage);
        context.RolePermissions.Add(new RolePermission(role.Id, extra.Id));
        await context.SaveChangesAsync();

        try
        {
            await Assert.ThrowsAsync<ProvisioningRefusedException>(
                () => new DevelopmentActorProvisioner(context, _hasher).ProvisionAsync(null, Now, CancellationToken.None));
        }
        finally
        {
            context.RolePermissions.Remove(await context.RolePermissions.SingleAsync(l => l.RoleId == role.Id && l.PermissionId == extra.Id));
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task ADemoUserIsCreatedActiveAndAnExistingEmailOrUnknownRoleIsRefused()
    {
        await using var context = fixture.CreateContext();
        var provisioner = new DevelopmentActorProvisioner(context, _hasher);
        var email = UniqueEmail("demo");

        await provisioner.CreateDemoUserAsync(email, "Demo", "User", "+201000000123", TestOnlyPassword, [], Now, CancellationToken.None);
        var user = await context.Users.SingleAsync(candidate => candidate.Email == email);

        Assert.Equal(UserStatus.Active, user.Status);
        await Assert.ThrowsAsync<ProvisioningRefusedException>(
            () => provisioner.CreateDemoUserAsync(email, "Demo", "User", "+201000000124", TestOnlyPassword, [], Now, CancellationToken.None));
        await Assert.ThrowsAsync<ProvisioningRefusedException>(
            () => provisioner.CreateDemoUserAsync(UniqueEmail("demo"), "Demo", "User", "+201000000125", TestOnlyPassword, ["No Such Role"], Now, CancellationToken.None));
    }

    // ---- grants

    [Fact]
    public async Task GrantsAreRecordedForTheRealActorsThroughTheDomainWithLabelledSimulatedProvenance()
    {
        await using var context = fixture.CreateContext();
        var world = await GrantWorldAsync(context);

        var report = await Provisioner(context).IssueAsync(
            world.PropertyId, world.OwnerEmail, DevelopmentAdminPassword.AdministratorEmail, DevelopmentActorProvisioner.LegalReviewerEmail,
            [ListingPurpose.Sale, ListingPurpose.Rent], 365, Now, CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var grants = await verify.PropertyAuthorityGrants.AsNoTracking().Where(g => g.PropertyId == world.PropertyId).ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, grant =>
        {
            Assert.Equal(world.OwnerId, grant.GranteeUserId);
            Assert.Equal(world.AdminId, grant.IssuedByUserId);
            Assert.Equal(world.LegalReviewerId, grant.LegalReviewedByUserId);
            Assert.Equal(ListingRepresentationKind.Owner, grant.Authority.Representation);
            Assert.Equal(world.OwnerProfileId, grant.Authority.DeclaredOwner.LegalPartyReferenceId);
            Assert.Equal(
                DevelopmentGrantProvisioner.SimulatedReviewReference(world.PropertyId, grant.Purpose, world.OwnerId, world.LegalReviewerId),
                grant.LegalReviewReferenceId);
        });
        Assert.Contains(report.Lines, line => line.Contains(DevelopmentGrantProvisioner.SimulatedLegalReviewLabel, StringComparison.Ordinal));
        Assert.Equal(
            [ListingPurpose.Sale, ListingPurpose.Rent],
            grants.Select(g => g.Purpose).Order().ToArray());
    }

    [Fact]
    public async Task IssuingAgainAddsNothingBecauseAnActiveGrantAlreadyExists()
    {
        await using var context = fixture.CreateContext();
        var world = await GrantWorldAsync(context);
        await Provisioner(context).IssueAsync(world.PropertyId, world.OwnerEmail, DevelopmentAdminPassword.AdministratorEmail, DevelopmentActorProvisioner.LegalReviewerEmail, [ListingPurpose.Sale], 30, Now, CancellationToken.None);

        var report = await Provisioner(context).IssueAsync(world.PropertyId, world.OwnerEmail, DevelopmentAdminPassword.AdministratorEmail, DevelopmentActorProvisioner.LegalReviewerEmail, [ListingPurpose.Sale], 30, Now, CancellationToken.None);

        Assert.Contains(report.Lines, line => line.Contains("already exists", StringComparison.Ordinal));
        Assert.Equal(1, await context.PropertyAuthorityGrants.CountAsync(g => g.PropertyId == world.PropertyId));
    }

    [Fact]
    public async Task TheIssuerAndTheLegalReviewerMustBeDifferentPeopleAndNeitherMayBeTheOwner()
    {
        await using var context = fixture.CreateContext();
        var world = await GrantWorldAsync(context);

        await RefusedAsync(context, world, issuer: DevelopmentAdminPassword.AdministratorEmail, reviewer: DevelopmentAdminPassword.AdministratorEmail);
        await RefusedAsync(context, world, issuer: world.OwnerEmail, reviewer: DevelopmentActorProvisioner.LegalReviewerEmail);
        await RefusedAsync(context, world, issuer: DevelopmentAdminPassword.AdministratorEmail, reviewer: world.OwnerEmail);
        Assert.Equal(0, await context.PropertyAuthorityGrants.CountAsync(g => g.PropertyId == world.PropertyId));
    }

    [Fact]
    public async Task AnIssuerOrLegalReviewerWithoutThePersistedPermissionIsRefused()
    {
        await using var context = fixture.CreateContext();
        var world = await GrantWorldAsync(context);
        var plain = await AddUserAsync(context, UniqueEmail("plain"));

        await RefusedAsync(context, world, issuer: plain.Email, reviewer: DevelopmentActorProvisioner.LegalReviewerEmail);
        await RefusedAsync(context, world, issuer: DevelopmentAdminPassword.AdministratorEmail, reviewer: plain.Email);
    }

    [Fact]
    public async Task AnOwnerWhoseProfileIsNotApprovedGetsNoGrant()
    {
        await using var context = fixture.CreateContext();
        var world = await GrantWorldAsync(context, approveProfile: false);

        await RefusedAsync(context, world, DevelopmentAdminPassword.AdministratorEmail, DevelopmentActorProvisioner.LegalReviewerEmail);
    }

    [Fact]
    public async Task AnIssuerWhoSharesAWorkspaceWithTheOwnerIsNotIndependentSoNoGrantIsIssued()
    {
        await using var context = fixture.CreateContext();
        var world = await GrantWorldAsync(context);
        var workspace = new Workspace(Guid.NewGuid(), "Shared " + Guid.NewGuid().ToString("N"), Now);
        context.Workspaces.Add(workspace);
        context.WorkspaceMemberships.Add(new WorkspaceMembership(Guid.NewGuid(), world.OwnerId, workspace.Id, Now));
        context.WorkspaceMemberships.Add(new WorkspaceMembership(Guid.NewGuid(), world.AdminId, workspace.Id, Now));
        await context.SaveChangesAsync();

        try
        {
            await RefusedAsync(context, world, DevelopmentAdminPassword.AdministratorEmail, DevelopmentActorProvisioner.LegalReviewerEmail);
        }
        finally
        {
            context.WorkspaceMemberships.RemoveRange(context.WorkspaceMemberships.Where(m => m.WorkspaceId == workspace.Id));
            await context.SaveChangesAsync();
        }
    }

    // ---- reviewer relationships

    [Fact]
    public async Task AnUnrelatedActiveReviewerIsIndependent()
    {
        await using var context = fixture.CreateContext();
        var (reviewer, submitter, owner) = await ThreePeopleAsync(context);

        var facts = await new ReviewerRelationshipChecker(context).CheckAsync(reviewer.Id, submitter.Id, Individual(owner.Id), null, default);

        Assert.True(facts.IdentitiesVerified);
        Assert.False(facts.ReviewerHasOwnerConflict);
        Assert.Null(facts.ReviewerAffiliatedWithWorkspace);
    }

    [Fact]
    public async Task TheSubmitterAndTheLinkedOwnerAreInConflictWithThemselves()
    {
        await using var context = fixture.CreateContext();
        var (_, submitter, owner) = await ThreePeopleAsync(context);
        var checker = new ReviewerRelationshipChecker(context);

        Assert.True((await checker.CheckAsync(submitter.Id, submitter.Id, Individual(owner.Id), null, default)).ReviewerHasOwnerConflict);
        Assert.True((await checker.CheckAsync(owner.Id, submitter.Id, Individual(owner.Id), null, default)).ReviewerHasOwnerConflict);
    }

    [Fact]
    public async Task ADifferentAccountIsNotEnoughWhenItSharesAWorkspaceWithTheSubmitterOrTheOwner()
    {
        await using var context = fixture.CreateContext();
        var (reviewerA, submitter, owner) = await ThreePeopleAsync(context);
        var reviewerB = await AddUserAsync(context, UniqueEmail("rb"));
        var workspaceA = new Workspace(Guid.NewGuid(), "A " + Guid.NewGuid().ToString("N"), Now);
        var workspaceB = new Workspace(Guid.NewGuid(), "B " + Guid.NewGuid().ToString("N"), Now);
        context.Workspaces.AddRange(workspaceA, workspaceB);
        context.WorkspaceMemberships.AddRange(
            new WorkspaceMembership(Guid.NewGuid(), reviewerA.Id, workspaceA.Id, Now),
            new WorkspaceMembership(Guid.NewGuid(), submitter.Id, workspaceA.Id, Now),
            new WorkspaceMembership(Guid.NewGuid(), reviewerB.Id, workspaceB.Id, Now),
            new WorkspaceMembership(Guid.NewGuid(), owner.Id, workspaceB.Id, Now));
        await context.SaveChangesAsync();
        var checker = new ReviewerRelationshipChecker(context);

        Assert.True((await checker.CheckAsync(reviewerA.Id, submitter.Id, Individual(owner.Id), null, default)).ReviewerHasOwnerConflict);
        Assert.True((await checker.CheckAsync(reviewerB.Id, submitter.Id, Individual(owner.Id), null, default)).ReviewerHasOwnerConflict);
    }

    [Fact]
    public async Task AnOrganisationOwnerOrAnOwnerWithNoAccountCannotBeShownIndependentSoTheAnswerIsUnknown()
    {
        await using var context = fixture.CreateContext();
        var (reviewer, submitter, _) = await ThreePeopleAsync(context);
        var checker = new ReviewerRelationshipChecker(context);

        var organisation = await checker.CheckAsync(reviewer.Id, submitter.Id, new ListingOwnerIdentity(ListingOwnerKind.Organization, Guid.NewGuid()), null, default);
        var noAccount = await checker.CheckAsync(reviewer.Id, submitter.Id, new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid()), null, default);

        Assert.Null(organisation.ReviewerHasOwnerConflict);
        Assert.Null(noAccount.ReviewerHasOwnerConflict);
    }

    [Fact]
    public async Task ASuspendedReviewerIsNotVerifiedAndAMemberOfTheSubmittingWorkspaceIsAffiliated()
    {
        await using var context = fixture.CreateContext();
        var (reviewer, submitter, owner) = await ThreePeopleAsync(context);
        var suspended = await AddUserAsync(context, UniqueEmail("susp"));
        suspended.Suspend(Now);
        var workspace = new Workspace(Guid.NewGuid(), "Sub " + Guid.NewGuid().ToString("N"), Now);
        context.Workspaces.Add(workspace);
        context.WorkspaceMemberships.Add(new WorkspaceMembership(Guid.NewGuid(), reviewer.Id, workspace.Id, Now));
        await context.SaveChangesAsync();
        var checker = new ReviewerRelationshipChecker(context);

        Assert.False((await checker.CheckAsync(suspended.Id, submitter.Id, Individual(owner.Id), null, default)).IdentitiesVerified);
        Assert.True((await checker.CheckAsync(reviewer.Id, submitter.Id, Individual(owner.Id), workspace.Id, default)).ReviewerAffiliatedWithWorkspace);
        Assert.False((await checker.CheckAsync(suspended.Id, submitter.Id, Individual(owner.Id), workspace.Id, default)).ReviewerAffiliatedWithWorkspace);
    }

    // ---- helpers

    private sealed record GrantWorld(Guid PropertyId, Guid OwnerId, string OwnerEmail, Guid OwnerProfileId, Guid AdminId, Guid LegalReviewerId);

    private DevelopmentGrantProvisioner Provisioner(ApplicationDbContext context) => new(context, new ReviewerRelationshipChecker(context));

    private async Task RefusedAsync(ApplicationDbContext context, GrantWorld world, string issuer, string reviewer)
    {
        await Assert.ThrowsAsync<ProvisioningRefusedException>(() => Provisioner(context).IssueAsync(
            world.PropertyId, world.OwnerEmail, issuer, reviewer, [ListingPurpose.Sale], 30, Now, CancellationToken.None));
        Assert.Equal(0, await context.PropertyAuthorityGrants.CountAsync(g => g.PropertyId == world.PropertyId));
    }

    private async Task<GrantWorld> GrantWorldAsync(ApplicationDbContext context, bool approveProfile = true)
    {
        var admin = await EnsureProvisionedAsync(context);
        var legalReviewer = await context.Users.SingleAsync(user => user.Email == DevelopmentActorProvisioner.LegalReviewerEmail);
        var owner = await AddUserAsync(context, UniqueEmail("owner"));
        var property = new Property(Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), Now.AddDays(-1));
        context.Properties.Add(property);
        var profile = new OwnerProfile(Guid.NewGuid(), owner.Id, Now.AddDays(-1));
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        profile.SaveIdentity(Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(), "Egyptian", new DateOnly(1990, 1, 1), today.AddYears(5), true, today, Now.AddDays(-1));
        profile.SaveAddress("1234", "Example Street", "Maadi", "Cairo", "11728", "5678", true, Now.AddDays(-1));
        profile.SetAddressProof("proof-key", "image/png", "proof.png", Now.AddDays(-1));
        profile.Submit(Now.AddDays(-1));

        if (approveProfile)
        {
            profile.Approve(admin.Id, Now.AddHours(-1));
        }

        context.OwnerProfiles.Add(profile);
        await context.SaveChangesAsync();

        return new GrantWorld(property.Id, owner.Id, owner.Email, profile.Id, admin.Id, legalReviewer.Id);
    }

    private async Task<User> EnsureProvisionedAsync(ApplicationDbContext context)
    {
        var admin = await EnsureAdministratorAsync(context);
        await new DevelopmentActorProvisioner(context, _hasher).ProvisionAsync(TestOnlyPassword, Now, CancellationToken.None);

        return admin;
    }

    private async Task<User> EnsureAdministratorAsync(ApplicationDbContext context)
    {
        var existing = await context.Users.SingleOrDefaultAsync(user => user.Email == DevelopmentAdminPassword.AdministratorEmail);

        return existing ?? await AddUserAsync(context, DevelopmentAdminPassword.AdministratorEmail);
    }

    private async Task<User> AddUserAsync(ApplicationDbContext context, string email)
    {
        var user = new User(Guid.NewGuid(), email, "Test", "User", Now.AddDays(-2));
        user.Activate(Now.AddDays(-2));
        context.Users.Add(user);
        context.UserCredentials.Add(new UserCredential(user.Id, _hasher.Hash(TestOnlyPassword), Now.AddDays(-2)));
        await context.SaveChangesAsync();

        return user;
    }

    private async Task<(User Reviewer, User Submitter, User Owner)> ThreePeopleAsync(ApplicationDbContext context) =>
        (await AddUserAsync(context, UniqueEmail("reviewer")), await AddUserAsync(context, UniqueEmail("submitter")), await AddUserAsync(context, UniqueEmail("owner")));

    private static ListingOwnerIdentity Individual(Guid userId) => new(ListingOwnerKind.Individual, Guid.NewGuid(), userId);

    private static string UniqueEmail(string prefix) => $"{prefix}.{Guid.NewGuid():n}@example.test";

    private static Task<string> HashOfAsync(ApplicationDbContext context, Guid userId) =>
        context.UserCredentials.AsNoTracking().Where(c => c.UserId == userId).Select(c => c.PasswordHash).SingleAsync();

    private static async Task<string[]> PermissionCodesOfAsync(ApplicationDbContext context, Guid userId) =>
        await context.UserPlatformRoles.AsNoTracking().Where(a => a.UserId == userId)
            .Join(context.RolePermissions, a => a.RoleId, l => l.RoleId, (_, l) => l.PermissionId)
            .Join(context.Permissions, id => id, p => p.Id, (_, p) => p.Code)
            .OrderBy(code => code).ToArrayAsync();
}
