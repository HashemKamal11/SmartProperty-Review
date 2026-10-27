using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class PropertyAuthorityGrantTests
{
    private static readonly DateTimeOffset At = MarketplaceFixtures.CreatedAt;
    private static ListingLegalAuthority Authority()
    {
        var grantee = Guid.NewGuid();
        return new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Sale,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), grantee), grantee,
            ListingRepresentationKind.Owner, null, At, At.AddDays(1));
    }
    private static ListingReviewContext Reviewer(ListingLegalAuthority authority, Guid? id = null,
        bool? verified = true, bool? authorized = true, bool? conflict = false) =>
        new(id ?? Guid.NewGuid(), authority.ActingUserId, authority.DeclaredOwner, null, verified, authorized, null, conflict);
    private static PropertyAuthorityGrant Issue(ListingLegalAuthority authority) => PropertyAuthorityGrant.Issue(
        Guid.NewGuid(), authority, Reviewer(authority), Guid.NewGuid(), Reviewer(authority), At, At.AddMinutes(1), true);

    [Fact]
    public void IssuedGrantCoversOnlyItsGranteePropertyPurposeAndInterval()
    {
        var authority = Authority();
        var grant = Issue(authority);
        grant.EnsureApplicable(authority.ActingUserId, authority.PropertyId, authority.Purpose, At.AddMinutes(1), 1);
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(Guid.NewGuid(), authority.PropertyId, authority.Purpose, At.AddHours(1), 1));
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(authority.ActingUserId, Guid.NewGuid(), authority.Purpose, At.AddHours(1), 1));
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(authority.ActingUserId, authority.PropertyId, ListingPurpose.Rent, At.AddHours(1), 1));
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(authority.ActingUserId, authority.PropertyId, authority.Purpose, At, 1));
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(authority.ActingUserId, authority.PropertyId, authority.Purpose, At.AddDays(1), 1));
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(authority.ActingUserId, authority.PropertyId, authority.Purpose, At.AddHours(1), 2));
    }

    [Theory]
    [InlineData("issuer_grantee")]
    [InlineData("reviewer_grantee")]
    [InlineData("same_reviewer")]
    [InlineData("unknown_identity")]
    [InlineData("unknown_permission")]
    [InlineData("unknown_conflict")]
    [InlineData("owner_conflict")]
    [InlineData("unknown_legal")]
    [InlineData("false_legal")]
    [InlineData("future_review")]
    [InlineData("wrong_owner")]
    public void IssuanceFailsClosed(string fault)
    {
        var authority = Authority();
        var issuer = Reviewer(authority, fault == "issuer_grantee" ? authority.ActingUserId : null,
            fault == "unknown_identity" ? null : true, fault == "unknown_permission" ? null : true,
            fault == "unknown_conflict" ? null : fault == "owner_conflict");
        var legal = Reviewer(fault == "wrong_owner" ? Authority() : authority,
            fault == "same_reviewer" ? issuer.ReviewerUserId : fault == "reviewer_grantee" ? authority.ActingUserId : null);
        Assert.Throws<InvalidOperationException>(() => PropertyAuthorityGrant.Issue(Guid.NewGuid(), authority,
            issuer, Guid.NewGuid(), legal, fault == "future_review" ? At.AddHours(1) : At, At.AddMinutes(1),
            fault == "unknown_legal" ? null : fault != "false_legal"));
    }

    [Fact]
    public void RevocationIsTerminalAndFailedRevocationDoesNotMutate()
    {
        var authority = Authority();
        var grant = Issue(authority);
        Assert.Throws<InvalidOperationException>(() => grant.Revoke(2, Reviewer(authority), At.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => grant.Revoke(1, Reviewer(authority, authorized: null), At.AddHours(1)));
        Assert.Null(grant.RevokedAt);
        Assert.Equal(1, grant.Version);
        var reviewer = Reviewer(authority);
        grant.Revoke(1, reviewer, At.AddHours(1));
        Assert.Equal(2, grant.Version);
        Assert.Equal(reviewer.ReviewerUserId, grant.RevokedByUserId);
        Assert.Throws<InvalidOperationException>(() => grant.Revoke(2, reviewer, At.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => grant.EnsureApplicable(authority.ActingUserId, authority.PropertyId, authority.Purpose, At.AddHours(2), 2));
        Assert.Equal(At.AddHours(1), grant.RevokedAt);
    }
}
