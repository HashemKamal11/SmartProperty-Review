#nullable enable
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Api.Infrastructure.Development;

/// <summary>What a provisioning command did, line by line, for the operator. Never contains a secret.</summary>
internal sealed class ProvisioningReport
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public void Add(string line) => _lines.Add(line);
}

/// <summary>Raised when a Development provisioning command refuses to act; the message says why and is safe to print.</summary>
internal sealed class ProvisioningRefusedException(string message) : Exception(message);

/// <summary>Reads persisted access: does a user hold a platform permission through a platform role.</summary>
internal static class PersistedAccess
{
    public static Task<bool> HoldsPlatformPermissionAsync(
        ApplicationDbContext context, Guid userId, string permissionCode, CancellationToken cancellationToken)
    {
        return context.UserPlatformRoles.AsNoTracking()
            .Where(assignment => assignment.UserId == userId)
            .Join(context.RolePermissions, assignment => assignment.RoleId, link => link.RoleId, (_, link) => link.PermissionId)
            .Join(context.Permissions, permissionId => permissionId, permission => permission.Id, (_, permission) => permission.Code)
            .AnyAsync(code => code == permissionCode, cancellationToken);
    }
}

/// <summary>
/// Resets the password of the Development administrator, and of nobody else.
/// </summary>
/// <remarks>
/// Uses the application's own password hasher, so the new hash is exactly what registration and reset would store. The
/// plaintext exists only as the argument and is never written, logged or returned. The account is fixed in code: this
/// type cannot be pointed at another user. Existing sessions are revoked, as the normal reset does.
/// </remarks>
internal sealed class DevelopmentAdminPassword(ApplicationDbContext context, IPasswordHasher hasher)
{
    public const string AdministratorEmail = "admin@smartproperty.local";

    public async Task ResetAsync(string newPassword, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length is < 8 or > 128)
        {
            throw new ProvisioningRefusedException("The password must be 8 to 128 characters.");
        }

        var user = await context.Users.SingleOrDefaultAsync(candidate => candidate.Email == AdministratorEmail, cancellationToken)
            ?? throw new ProvisioningRefusedException($"There is no account {AdministratorEmail}.");

        if (user.Status != UserStatus.Active)
        {
            throw new ProvisioningRefusedException($"{AdministratorEmail} is not Active, so its password is left alone.");
        }

        var credential = await context.UserCredentials.SingleOrDefaultAsync(candidate => candidate.UserId == user.Id, cancellationToken)
            ?? throw new ProvisioningRefusedException($"{AdministratorEmail} has no credential record.");

        credential.ChangePasswordHash(hasher.Hash(newPassword), now);

        foreach (var token in await context.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(cancellationToken))
        {
            token.Revoke(now);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Creates the Development roles, the Development legal reviewer and, when asked, other demo accounts, with only the
/// permissions each one actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Least privilege, spelled out.</b> The Development administrator keeps the Platform Admin role it already has and
/// additionally receives one dedicated role holding exactly <c>marketplace.settings.manage</c> and
/// <c>marketplace.authority.grants.issue</c> (the issuer's authority). The legal reviewer receives one dedicated role holding
/// exactly <c>marketplace.authority.legal_reviews.record</c> and nothing else: no property, review, owner-verification or
/// administrative permission. The shared Platform Admin role is not touched.
/// </para>
/// <para>Idempotent: running it again changes nothing that already exists.</para>
/// </remarks>
internal sealed class DevelopmentActorProvisioner(ApplicationDbContext context, IPasswordHasher hasher)
{
    public const string LegalReviewerEmail = "dev.legal.reviewer@smartproperty.local";
    public const string AdministratorRoleName = "Development Marketplace Administrator";
    public const string LegalReviewerRoleName = "Development Legal Reviewer";

    public async Task<ProvisioningReport> ProvisionAsync(string? legalReviewerPassword, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var report = new ProvisioningReport();

        var administrator = await context.Users.SingleOrDefaultAsync(user => user.Email == DevelopmentAdminPassword.AdministratorEmail, cancellationToken)
            ?? throw new ProvisioningRefusedException($"There is no account {DevelopmentAdminPassword.AdministratorEmail}.");

        if (administrator.Status != UserStatus.Active)
        {
            throw new ProvisioningRefusedException($"{DevelopmentAdminPassword.AdministratorEmail} is not Active.");
        }

        var settings = await EnsurePermissionAsync(PermissionCodes.MarketplaceSettingsManage, PermissionCodes.MarketplaceSettingsManageDescription, now, report, cancellationToken);
        var issue = await EnsurePermissionAsync(PermissionCodes.MarketplaceAuthorityGrantsIssue, PermissionCodes.MarketplaceAuthorityGrantsIssueDescription, now, report, cancellationToken);
        var record = await EnsurePermissionAsync(PermissionCodes.MarketplaceAuthorityLegalReviewsRecord, PermissionCodes.MarketplaceAuthorityLegalReviewsRecordDescription, now, report, cancellationToken);

        var administratorRole = await EnsureRoleAsync(AdministratorRoleName, [settings, issue], now, report, cancellationToken);
        var legalReviewerRole = await EnsureRoleAsync(LegalReviewerRoleName, [record], now, report, cancellationToken);

        await EnsureAssignmentAsync(administrator.Id, administratorRole, DevelopmentAdminPassword.AdministratorEmail, report, cancellationToken);

        var legalReviewer = await context.Users.SingleOrDefaultAsync(user => user.Email == LegalReviewerEmail, cancellationToken);

        if (legalReviewer is null)
        {
            if (string.IsNullOrEmpty(legalReviewerPassword))
            {
                throw new ProvisioningRefusedException($"{LegalReviewerEmail} does not exist yet and no password was supplied for it.");
            }

            legalReviewer = await CreateUserAsync(LegalReviewerEmail, "Dev", "Legal Reviewer (simulated)", "+201099999999", legalReviewerPassword, now, cancellationToken);
            report.Add($"Created the Development legal reviewer {LegalReviewerEmail} (Active, contacts marked verified because registration is bypassed here).");
        }
        else
        {
            report.Add($"The Development legal reviewer {LegalReviewerEmail} already exists; its password is left alone.");
        }

        await EnsureAssignmentAsync(legalReviewer.Id, legalReviewerRole, LegalReviewerEmail, report, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return report;
    }

    /// <summary>
    /// Creates an Active demo account and, optionally, gives it existing platform roles by name. Refuses an email that
    /// exists and any role that does not.
    /// </summary>
    public async Task<ProvisioningReport> CreateDemoUserAsync(
        string email, string firstName, string lastName, string phoneNumber, string password,
        IReadOnlyCollection<string> platformRoleNames, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var report = new ProvisioningReport();
        var normalized = email.Trim().ToLowerInvariant();

        if (await context.Users.AnyAsync(user => user.Email == normalized, cancellationToken))
        {
            throw new ProvisioningRefusedException($"{normalized} already exists; nothing was changed.");
        }

        var roles = new List<Role>();

        foreach (var name in platformRoleNames)
        {
            roles.Add(await context.Roles.SingleOrDefaultAsync(role => role.Scope == RoleScope.Platform && role.Name == name, cancellationToken)
                ?? throw new ProvisioningRefusedException($"There is no platform role '{name}'."));
        }

        var user = await CreateUserAsync(normalized, firstName, lastName, phoneNumber, password, now, cancellationToken);
        report.Add($"Created demo account {normalized} (Active).");

        foreach (var role in roles)
        {
            context.UserPlatformRoles.Add(new UserPlatformRole(user.Id, role.Id));
            report.Add($"Gave {normalized} the platform role '{role.Name}'.");
        }

        await context.SaveChangesAsync(cancellationToken);

        return report;
    }

    private async Task<User> CreateUserAsync(
        string email, string firstName, string lastName, string phoneNumber, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (password.Length is < 8 or > 128)
        {
            throw new ProvisioningRefusedException("The password must be 8 to 128 characters.");
        }

        var user = new User(Guid.NewGuid(), email, firstName, lastName, now, phoneNumber);
        user.VerifyPhone(now);
        user.VerifyEmail(now);
        user.Activate(now);

        context.Users.Add(user);
        context.UserCredentials.Add(new UserCredential(user.Id, hasher.Hash(password), now));
        await context.SaveChangesAsync(cancellationToken);

        return user;
    }

    private async Task<Permission> EnsurePermissionAsync(string code, string description, DateTimeOffset now, ProvisioningReport report, CancellationToken cancellationToken)
    {
        var existing = await context.Permissions.SingleOrDefaultAsync(permission => permission.Code == code, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = new Permission(Guid.NewGuid(), code, description);
        context.Permissions.Add(created);
        await context.SaveChangesAsync(cancellationToken);
        report.Add($"Created the permission {code}.");

        return created;
    }

    private async Task<Role> EnsureRoleAsync(string name, IReadOnlyCollection<Permission> permissions, DateTimeOffset now, ProvisioningReport report, CancellationToken cancellationToken)
    {
        var role = await context.Roles.SingleOrDefaultAsync(candidate => candidate.Scope == RoleScope.Platform && candidate.Name == name, cancellationToken);

        if (role is null)
        {
            role = new Role(Guid.NewGuid(), name, RoleScope.Platform, workspaceId: null, now);
            context.Roles.Add(role);
            await context.SaveChangesAsync(cancellationToken);
            report.Add($"Created the platform role '{name}'.");
        }

        var held = await context.RolePermissions.Where(link => link.RoleId == role.Id).Select(link => link.PermissionId).ToListAsync(cancellationToken);

        // Exactly the intended permissions: an unexpected extra one on a Development role is refused, not tolerated.
        if (held.Except(permissions.Select(permission => permission.Id)).Any())
        {
            throw new ProvisioningRefusedException($"The role '{name}' already holds permissions beyond the intended ones; nothing was changed.");
        }

        foreach (var permission in permissions.Where(permission => !held.Contains(permission.Id)))
        {
            context.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            report.Add($"Role '{name}' now holds {permission.Code}.");
        }

        await context.SaveChangesAsync(cancellationToken);

        return role;
    }

    private async Task EnsureAssignmentAsync(Guid userId, Role role, string email, ProvisioningReport report, CancellationToken cancellationToken)
    {
        if (await context.UserPlatformRoles.AnyAsync(assignment => assignment.UserId == userId && assignment.RoleId == role.Id, cancellationToken))
        {
            return;
        }

        context.UserPlatformRoles.Add(new UserPlatformRole(userId, role.Id));
        report.Add($"Gave {email} the platform role '{role.Name}'.");
    }
}

/// <summary>
/// Records Sale and Rent grants for the real demo actors, through the Domain's own rules, because the platform has no
/// grant-issuing workflow.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The grantee, the issuer and the legal reviewer are real accounts, and the three must be distinct,
/// <c>Active</c> and free of the relationships the independence check looks for (no shared workspace with the owner). The
/// issuer must hold the persisted <c>marketplace.authority.grants.issue</c> permission and the legal reviewer the persisted
/// <c>marketplace.authority.legal_reviews.record</c> permission. The declared owner is the owner's own <b>approved</b> owner
/// profile (its id is the legal-party reference). The Domain's <c>PropertyAuthorityGrant.Issue</c> then validates everything
/// it always validates.
/// </para>
/// <para>
/// <b>What is simulated.</b> No external legal review exists. The legal-review reference is a deterministic identifier
/// derived from the label <see cref="SimulatedLegalReviewLabel"/> and the grant's parties, so it is recognisable and
/// reproducible, and the command says so every time it runs. It is not a record of any real legal review.
/// </para>
/// </remarks>
internal sealed class DevelopmentGrantProvisioner(ApplicationDbContext context, ReviewerRelationshipChecker checker)
{
    public const string SimulatedLegalReviewLabel = "SIMULATED-DEV-LEGAL-REVIEW";

    public async Task<ProvisioningReport> IssueAsync(
        Guid propertyId,
        string ownerEmail,
        string issuerEmail,
        string legalReviewerEmail,
        IReadOnlyCollection<ListingPurpose> purposes,
        int validDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var report = new ProvisioningReport();
        report.Add($"{SimulatedLegalReviewLabel}: no external legal review exists. The review reference recorded on each grant is a labelled simulation.");

        if (purposes.Count == 0 || purposes.Distinct().Count() != purposes.Count)
        {
            throw new ProvisioningRefusedException("Name each purpose once: Sale, Rent, or both.");
        }

        if (validDays is < 1 or > 3650)
        {
            throw new ProvisioningRefusedException("The validity must be 1 to 3650 days.");
        }

        var owner = await RequireActiveUserAsync(ownerEmail, "owner", cancellationToken);
        var issuer = await RequireActiveUserAsync(issuerEmail, "issuer", cancellationToken);
        var legalReviewer = await RequireActiveUserAsync(legalReviewerEmail, "legal reviewer", cancellationToken);

        if (new[] { owner.Id, issuer.Id, legalReviewer.Id }.Distinct().Count() != 3)
        {
            throw new ProvisioningRefusedException("The owner, the issuer and the legal reviewer must be three different accounts.");
        }

        var property = await context.Properties.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == propertyId, cancellationToken)
            ?? throw new ProvisioningRefusedException("That property does not exist.");

        if (property.Status != SmartProperty.Domain.PropertyRegistry.PropertyStatus.Active)
        {
            throw new ProvisioningRefusedException("That property is not Active.");
        }

        var profile = await context.OwnerProfiles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.UserId == owner.Id, cancellationToken);

        if (profile is null || profile.Status != OwnerVerificationStatus.Approved)
        {
            throw new ProvisioningRefusedException("The owner's owner profile must be Approved first: it is the legal-party record the grant names.");
        }

        var issuerHolds = await PersistedAccess.HoldsPlatformPermissionAsync(context, issuer.Id, PermissionCodes.MarketplaceAuthorityGrantsIssue, cancellationToken);
        var reviewerHolds = await PersistedAccess.HoldsPlatformPermissionAsync(context, legalReviewer.Id, PermissionCodes.MarketplaceAuthorityLegalReviewsRecord, cancellationToken);

        if (!issuerHolds)
        {
            throw new ProvisioningRefusedException($"The issuer does not hold {PermissionCodes.MarketplaceAuthorityGrantsIssue}. Run the provision command first.");
        }

        if (!reviewerHolds)
        {
            throw new ProvisioningRefusedException($"The legal reviewer does not hold {PermissionCodes.MarketplaceAuthorityLegalReviewsRecord}. Run the provision command first.");
        }

        var declaredOwner = new ListingOwnerIdentity(ListingOwnerKind.Individual, profile.Id, owner.Id);

        // The same relationship checks the listing reviewer faces, applied to both people behind a grant.
        var issuerFacts = await checker.CheckAsync(issuer.Id, owner.Id, declaredOwner, null, cancellationToken);
        var reviewerFacts = await checker.CheckAsync(legalReviewer.Id, owner.Id, declaredOwner, null, cancellationToken);

        RequireIndependent(issuerFacts, "issuer");
        RequireIndependent(reviewerFacts, "legal reviewer");

        var issuerContext = new ListingReviewContext(issuer.Id, owner.Id, declaredOwner, null, issuerFacts.IdentitiesVerified, issuerHolds, null, issuerFacts.ReviewerHasOwnerConflict);
        var reviewerContext = new ListingReviewContext(legalReviewer.Id, owner.Id, declaredOwner, null, reviewerFacts.IdentitiesVerified, reviewerHolds, null, reviewerFacts.ReviewerHasOwnerConflict);

        foreach (var purpose in purposes)
        {
            var active = await context.PropertyAuthorityGrants.AsNoTracking()
                .AnyAsync(grant => grant.GranteeUserId == owner.Id && grant.PropertyId == propertyId && grant.Purpose == purpose && grant.RevokedAt == null, cancellationToken);

            if (active)
            {
                report.Add($"A {purpose} grant for {ownerEmail} on this property already exists; nothing was added for it.");

                continue;
            }

            var authority = new ListingLegalAuthority(
                propertyId, purpose, declaredOwner, owner.Id, ListingRepresentationKind.Owner, null, now.AddMinutes(-1), now.AddDays(validDays));
            var grant = PropertyAuthorityGrant.Issue(
                Guid.NewGuid(), authority, issuerContext, SimulatedReviewReference(propertyId, purpose, owner.Id, legalReviewer.Id), reviewerContext,
                now.AddSeconds(-2), now.AddSeconds(-1), legalAuthorityVerified: true);

            context.PropertyAuthorityGrants.Add(grant);
            report.Add($"Issued a {purpose} grant {grant.Id} to {ownerEmail} for the property, valid {validDays} days; issuer {issuerEmail}; legal reviewer {legalReviewerEmail} ({SimulatedLegalReviewLabel}).");
        }

        await context.SaveChangesAsync(cancellationToken);

        return report;
    }

    /// <summary>A recognisable, reproducible identifier for a simulated review. Not a record of anything external.</summary>
    internal static Guid SimulatedReviewReference(Guid propertyId, ListingPurpose purpose, Guid ownerId, Guid legalReviewerId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{SimulatedLegalReviewLabel}|{propertyId:N}|{purpose}|{ownerId:N}|{legalReviewerId:N}"));

        return new Guid(digest.AsSpan(0, 16));
    }

    private async Task<User> RequireActiveUserAsync(string email, string role, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Email == normalized, cancellationToken)
            ?? throw new ProvisioningRefusedException($"There is no {role} account {normalized}.");

        return user.Status == UserStatus.Active
            ? user
            : throw new ProvisioningRefusedException($"The {role} account {normalized} is not Active.");
    }

    private static void RequireIndependent(ReviewerIndependenceFacts facts, string who)
    {
        if (facts.IdentitiesVerified != true)
        {
            throw new ProvisioningRefusedException($"The {who} account is not Active.");
        }

        if (facts.ReviewerHasOwnerConflict != false)
        {
            throw new ProvisioningRefusedException($"The {who} is the owner, or shares a workspace with the owner, or cannot be shown to be independent of the owner.");
        }
    }
}
