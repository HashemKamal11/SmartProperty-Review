using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingLegalAuthorityTests
{
    private static readonly DateTimeOffset Start = MarketplaceFixtures.CreatedAt;

    [Theory]
    [InlineData(ListingRepresentationKind.Owner, ListingPurpose.Sale)]
    [InlineData(ListingRepresentationKind.Owner, ListingPurpose.Rent)]
    [InlineData(ListingRepresentationKind.IndividualRepresentative, ListingPurpose.Sale)]
    [InlineData(ListingRepresentationKind.WorkspaceRepresentative, ListingPurpose.Rent)]
    public void VerifiedAuthority_CoversOnlyItsDeclaredContext(ListingRepresentationKind kind, ListingPurpose purpose)
    {
        var owner = Guid.NewGuid();
        var actor = kind == ListingRepresentationKind.Owner ? owner : Guid.NewGuid();
        Guid? workspace = kind == ListingRepresentationKind.WorkspaceRepresentative ? Guid.NewGuid() : null;
        var authority = new ListingLegalAuthority(Guid.NewGuid(), purpose, Owner(owner), actor, kind, workspace, Start, Start.AddDays(1));

        authority.EnsureApplicable(authority.PropertyId, purpose, actor, workspace, Start, true, true);
        Assert.Equal(owner, authority.DeclaredOwner.LinkedUserId);
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            Guid.NewGuid(), purpose, actor, workspace, Start, true, true));
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, purpose == ListingPurpose.Sale ? ListingPurpose.Rent : ListingPurpose.Sale,
            actor, workspace, Start, true, true));
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, purpose, Guid.NewGuid(), workspace, Start, true, true));
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, purpose, actor, Guid.NewGuid(), Start, true, true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    public void WorkspaceOperationAndLegalVerification_AreSeparatelyRequired(bool? legal, bool? operational)
    {
        var authority = new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Sale, Owner(Guid.NewGuid()), Guid.NewGuid(),
            ListingRepresentationKind.WorkspaceRepresentative, Guid.NewGuid(), Start, null);

        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, authority.Purpose, authority.ActingUserId, authority.WorkspaceId, Start, legal, operational));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void OwnerDeclaration_DoesNotProveOwnership(bool? verified)
    {
        var owner = Guid.NewGuid();
        var authority = new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Rent, Owner(owner), owner,
            ListingRepresentationKind.Owner, null, Start, null);

        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, authority.Purpose, owner, null, Start, verified, null));
    }

    [Fact]
    public void Validity_IsStartInclusiveAndEndExclusive()
    {
        var owner = Guid.NewGuid();
        var end = Start.AddHours(1);
        var authority = new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Sale, Owner(owner), owner,
            ListingRepresentationKind.Owner, null, Start, end);

        authority.EnsureApplicable(authority.PropertyId, authority.Purpose, owner, null, Start, true, null);
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, authority.Purpose, owner, null, Start.AddTicks(-1), true, null));
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, authority.Purpose, owner, null, end, true, null));
    }

    [Fact]
    public void Constructor_RejectsInvalidIdentityEnumsContextAndInterval()
    {
        var owner = Guid.NewGuid();
        var property = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(Guid.Empty, ListingPurpose.Sale, Owner(owner), owner,
            ListingRepresentationKind.Owner, null, Start, null));
        Assert.Throws<ArgumentNullException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, null!, owner,
            ListingRepresentationKind.IndividualRepresentative, null, Start, null));
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), Guid.Empty,
            ListingRepresentationKind.IndividualRepresentative, null, Start, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingLegalAuthority(property, (ListingPurpose)99, Owner(owner), owner,
            ListingRepresentationKind.Owner, null, Start, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), owner,
            (ListingRepresentationKind)99, null, Start, null));
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), Guid.NewGuid(),
            ListingRepresentationKind.Owner, null, Start, null));
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), owner,
            ListingRepresentationKind.Owner, Guid.NewGuid(), Start, null));
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), owner,
            ListingRepresentationKind.WorkspaceRepresentative, null, Start, null));
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), owner,
            ListingRepresentationKind.WorkspaceRepresentative, Guid.Empty, Start, null));
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(property, ListingPurpose.Sale, Owner(owner), owner,
            ListingRepresentationKind.Owner, null, Start, Start));
    }

    [Theory]
    [InlineData(ListingOwnerKind.Individual)]
    [InlineData(ListingOwnerKind.Organization)]
    public void UnlinkedOwners_CanUseVerifiedRepresentatives(ListingOwnerKind kind)
    {
        var owner = new ListingOwnerIdentity(kind, Guid.NewGuid());
        var authority = new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Rent, owner, Guid.NewGuid(),
            ListingRepresentationKind.IndividualRepresentative, null, Start, null);
        authority.EnsureApplicable(authority.PropertyId, authority.Purpose, authority.ActingUserId,
            null, Start, true, null);
        Assert.Same(owner, authority.DeclaredOwner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void UnlinkedOwnerRole_RequiresExplicitActorMatch(bool? match)
    {
        var authority = new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Sale,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid()), Guid.NewGuid(),
            ListingRepresentationKind.Owner, null, Start, null);
        Assert.Throws<InvalidOperationException>(() => authority.EnsureApplicable(
            authority.PropertyId, authority.Purpose, authority.ActingUserId, null, Start, true, null, match));
        authority.EnsureApplicable(authority.PropertyId, authority.Purpose, authority.ActingUserId,
            null, Start, true, null, true);
    }

    [Fact]
    public void Organization_CannotUseDirectOwnerRole()
    {
        Assert.Throws<ArgumentException>(() => new ListingLegalAuthority(Guid.NewGuid(), ListingPurpose.Sale,
            new ListingOwnerIdentity(ListingOwnerKind.Organization, Guid.NewGuid()), Guid.NewGuid(),
            ListingRepresentationKind.Owner, null, Start, null));
    }

    private static ListingOwnerIdentity Owner(Guid userId) =>
        new(ListingOwnerKind.Individual, Guid.NewGuid(), userId);
}
