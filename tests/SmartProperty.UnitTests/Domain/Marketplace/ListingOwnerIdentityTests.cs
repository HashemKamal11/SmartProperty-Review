using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingOwnerIdentityTests
{
    [Theory]
    [InlineData(ListingOwnerKind.Individual, false)]
    [InlineData(ListingOwnerKind.Individual, true)]
    [InlineData(ListingOwnerKind.Organization, false)]
    public void Constructor_PreservesDeclaredIdentity(ListingOwnerKind kind, bool linked)
    {
        var party = Guid.NewGuid();
        Guid? user = linked ? Guid.NewGuid() : null;
        var owner = new ListingOwnerIdentity(kind, party, user);
        Assert.Equal(kind, owner.Kind);
        Assert.Equal(party, owner.LegalPartyReferenceId);
        Assert.Equal(user, owner.LinkedUserId);
    }

    [Fact]
    public void Constructor_RejectsInvalidIdentityAndOrganizationUserLink()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingOwnerIdentity((ListingOwnerKind)99, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ListingOwnerIdentity(ListingOwnerKind.Organization, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Identity_HasNoSettersAndDoesNotPrintProtectedReference()
    {
        var owner = new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid());
        Assert.All(typeof(ListingOwnerIdentity).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.DoesNotContain(owner.LegalPartyReferenceId.ToString(), owner.ToString()!);
    }
}
