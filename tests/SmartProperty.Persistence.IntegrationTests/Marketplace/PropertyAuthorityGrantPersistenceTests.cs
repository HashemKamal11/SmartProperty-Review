using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// A grant the Domain validated survives real PostgreSQL unchanged, is immutable except for its revocation, and at
/// most one grant per user, property and purpose is in force. Nothing here issues a grant through the application:
/// the issuance workflow is not decided, so these tests issue through the Domain directly.
/// </summary>
public sealed class PropertyAuthorityGrantPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset T0 = TestClock.DefaultNow;

    [Fact]
    public async Task AnIssuedGrantRoundTripsWithItsAuthorityAndProvenance()
    {
        var world = await SeedAsync();
        var grant = NewGrant(world);
        await SaveAsync(grant);

        var stored = await ReloadAsync(grant.Id);

        Assert.Equal(world.PropertyId, stored.PropertyId);
        Assert.Equal(ListingPurpose.Sale, stored.Purpose);
        Assert.Equal(world.Grantee, stored.GranteeUserId);
        Assert.Equal(world.Issuer, stored.IssuedByUserId);
        Assert.Equal(world.LegalReviewer, stored.LegalReviewedByUserId);
        Assert.Equal(grant.LegalReviewReferenceId, stored.LegalReviewReferenceId);
        Assert.Equal(grant.IssuedAt, stored.IssuedAt);
        Assert.Equal(grant.LegalReviewedAt, stored.LegalReviewedAt);
        Assert.Equal(1, stored.Version);
        Assert.Null(stored.RevokedAt);
        Assert.Equal(ListingRepresentationKind.Owner, stored.Authority.Representation);
        Assert.Equal(grant.Authority.ValidUntil, stored.Authority.ValidUntil);
        Assert.Equal(world.Grantee, stored.Authority.DeclaredOwner.LinkedUserId);
        Assert.True(stored.Issuer.PlatformReviewAuthorized);
        Assert.Equal(world.Issuer, stored.Issuer.ReviewerUserId);
    }

    [Fact]
    public async Task ARevocationPersistsAndBumpsTheVersion()
    {
        var world = await SeedAsync();
        var grant = NewGrant(world);
        await SaveAsync(grant);

        await using (var context = Host.CreateVerificationContext())
        {
            var tracked = await context.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
            tracked.Revoke(1, Reviewer(tracked, world.Extra), T0.AddDays(3));
            await context.SaveChangesAsync();
        }

        var stored = await ReloadAsync(grant.Id);
        Assert.Equal(T0.AddDays(3), stored.RevokedAt);
        Assert.Equal(world.Extra, stored.RevokedByUserId);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task ARevokedGrantStaysAsHistoryAndAFreshGrantMayBeIssued()
    {
        var world = await SeedAsync();
        var first = NewGrant(world);
        await SaveAsync(first);

        await using (var context = Host.CreateVerificationContext())
        {
            var tracked = await context.PropertyAuthorityGrants.SingleAsync(g => g.Id == first.Id);
            tracked.Revoke(1, Reviewer(tracked, world.Extra), T0.AddDays(3));
            await context.SaveChangesAsync();
        }

        var second = NewGrant(world);
        await SaveAsync(second);

        await using var reading = Host.CreateVerificationContext();
        var all = await reading.PropertyAuthorityGrants.AsNoTracking()
            .Where(g => g.GranteeUserId == world.Grantee).OrderBy(g => g.IssuedAt).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.NotNull(all[0].RevokedAt);
        Assert.Null(all[1].RevokedAt);
    }

    [Fact]
    public async Task TwoGrantsInForceForTheSameUserPropertyAndPurposeAreRefusedByTheDatabase()
    {
        var world = await SeedAsync();
        await SaveAsync(NewGrant(world));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(NewGrant(world)));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_marketplace_authority_grants_active", postgres.ConstraintName);
    }

    [Fact]
    public async Task ADifferentPurposeIsADifferentGrant()
    {
        var world = await SeedAsync();
        await SaveAsync(NewGrant(world, ListingPurpose.Sale));

        await SaveAsync(NewGrant(world, ListingPurpose.Rent));

        await using var reading = Host.CreateVerificationContext();
        Assert.Equal(2, await reading.PropertyAuthorityGrants.CountAsync(g => g.GranteeUserId == world.Grantee));
    }

    [Fact]
    public async Task TwoRevocationsRacingOnTheSameVersionLetOnlyOneWin()
    {
        var world = await SeedAsync();
        var grant = NewGrant(world);
        await SaveAsync(grant);

        await using var first = Host.CreateVerificationContext();
        await using var second = Host.CreateVerificationContext();
        var a = await first.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
        var b = await second.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
        a.Revoke(1, Reviewer(a, world.Extra), T0.AddDays(3));
        b.Revoke(1, Reviewer(b, world.Extra), T0.AddDays(4));

        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TheSubjectAndProvenanceOfAGrantCannotBeChangedAndAGrantCannotBeDeleted()
    {
        var world = await SeedAsync();
        var grant = NewGrant(world);
        await SaveAsync(grant);

        await using (var context = Host.CreateVerificationContext())
        {
            var tracked = await context.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
            context.Entry(tracked).Property(g => g.GranteeUserId).CurrentValue = world.Extra;

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }

        await using (var context = Host.CreateVerificationContext())
        {
            var tracked = await context.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
            context.PropertyAuthorityGrants.Remove(tracked);

            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }

        Assert.Equal(world.Grantee, (await ReloadAsync(grant.Id)).GranteeUserId);
    }

    [Fact]
    public async Task ARevokedGrantCannotBeRevokedAgainEvenAroundTheDomain()
    {
        var world = await SeedAsync();
        var grant = NewGrant(world);
        await SaveAsync(grant);

        await using (var context = Host.CreateVerificationContext())
        {
            var tracked = await context.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
            tracked.Revoke(1, Reviewer(tracked, world.Extra), T0.AddDays(3));
            await context.SaveChangesAsync();
        }

        await using var again = Host.CreateVerificationContext();
        var revoked = await again.PropertyAuthorityGrants.SingleAsync(g => g.Id == grant.Id);
        again.Entry(revoked).Property(g => g.RevokedAt).CurrentValue = T0.AddDays(9);

        await Assert.ThrowsAsync<InvalidOperationException>(() => again.SaveChangesAsync());
    }

    // ---- helpers

    private sealed record World(Guid PropertyId, Guid Grantee, Guid Issuer, Guid LegalReviewer, Guid Extra);

    private async Task<World> SeedAsync()
    {
        var users = Enumerable.Range(0, 4)
            .Select(_ => new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "User", T0))
            .ToArray();
        var property = new Property(Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), T0);

        await using var context = Host.CreateVerificationContext();
        context.Users.AddRange(users);
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return new World(property.Id, users[0].Id, users[1].Id, users[2].Id, users[3].Id);
    }

    private static ListingOwnerIdentity Owner(World world) =>
        new(ListingOwnerKind.Individual, Guid.NewGuid(), world.Grantee);

    // A reviewer context must describe the same declared owner as the grant it reviews or revokes.
    private static ListingReviewContext Reviewer(PropertyAuthorityGrant grant, Guid reviewer) =>
        new(reviewer, grant.GranteeUserId, grant.Authority.DeclaredOwner, null, true, true, null, false);

    private static PropertyAuthorityGrant NewGrant(World world, ListingPurpose purpose = ListingPurpose.Sale)
    {
        var owner = Owner(world);
        var authority = new ListingLegalAuthority(
            world.PropertyId, purpose, owner, world.Grantee, ListingRepresentationKind.Owner, null, T0, T0.AddYears(1));
        var issuer = new ListingReviewContext(world.Issuer, world.Grantee, owner, null, true, true, null, false);
        var legal = new ListingReviewContext(world.LegalReviewer, world.Grantee, owner, null, true, true, null, false);

        return PropertyAuthorityGrant.Issue(
            Guid.NewGuid(), authority, issuer, Guid.NewGuid(), legal, T0.AddHours(1), T0.AddHours(2), legalAuthorityVerified: true);
    }

    private async Task SaveAsync(PropertyAuthorityGrant grant)
    {
        await using var context = Host.CreateVerificationContext();
        context.PropertyAuthorityGrants.Add(grant);
        await context.SaveChangesAsync();
    }

    private async Task<PropertyAuthorityGrant> ReloadAsync(Guid id)
    {
        await using var context = Host.CreateVerificationContext();
        return await context.PropertyAuthorityGrants.AsNoTracking().SingleAsync(g => g.Id == id);
    }
}
