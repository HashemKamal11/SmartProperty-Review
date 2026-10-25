using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingIndependentReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentAuthorizedReviewer_IsEligible(bool workspaceSubmission)
    {
        var context = new ListingReviewContext(Guid.NewGuid(), Guid.NewGuid(), Owner(Guid.NewGuid()),
            workspaceSubmission ? Guid.NewGuid() : null, true, true, workspaceSubmission ? false : null, false);
        context.EnsureIndependentReviewer();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReviewerCannotBeSubmitterOrOwner(bool isSubmitter)
    {
        var reviewer = Guid.NewGuid();
        var context = new ListingReviewContext(reviewer, isSubmitter ? reviewer : Guid.NewGuid(),
            Owner(isSubmitter ? Guid.NewGuid() : reviewer), null, true, true, null, false);
        Assert.Throws<InvalidOperationException>(() => context.EnsureIndependentReviewer());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    public void MissingOrNegativeVerification_FailsClosed(bool? identities, bool? platformAuthorization)
    {
        var context = new ListingReviewContext(Guid.NewGuid(), Guid.NewGuid(), Owner(Guid.NewGuid()),
            null, identities, platformAuthorization, null, false);
        Assert.Throws<InvalidOperationException>(() => context.EnsureIndependentReviewer());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void WorkspaceAffiliation_MustBeExplicitlyVerifiedAbsent(bool? affiliated)
    {
        var context = new ListingReviewContext(Guid.NewGuid(), Guid.NewGuid(), Owner(Guid.NewGuid()),
            Guid.NewGuid(), true, true, affiliated, false);
        Assert.Throws<InvalidOperationException>(() => context.EnsureIndependentReviewer());
    }

    [Fact]
    public void Constructor_RejectsMissingIdentityAndInconsistentWorkspaceFacts()
    {
        var reviewer = Guid.NewGuid();
        var submitter = Guid.NewGuid();
        var owner = Owner(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new ListingReviewContext(Guid.Empty, submitter, owner, null, true, true, null, false));
        Assert.Throws<ArgumentException>(() => new ListingReviewContext(reviewer, Guid.Empty, owner, null, true, true, null, false));
        Assert.Throws<ArgumentNullException>(() => new ListingReviewContext(reviewer, submitter, null!, null, true, true, null, false));
        Assert.Throws<ArgumentException>(() => new ListingReviewContext(reviewer, submitter, owner, Guid.Empty, true, true, false, false));
        Assert.Throws<ArgumentException>(() => new ListingReviewContext(reviewer, submitter, owner, null, true, true, false, false));
    }

    [Theory]
    [InlineData(ListingOwnerKind.Individual, null)]
    [InlineData(ListingOwnerKind.Individual, true)]
    [InlineData(ListingOwnerKind.Organization, null)]
    [InlineData(ListingOwnerKind.Organization, true)]
    public void UnlinkedOwnerConflict_MustBeExplicitlyAbsent(ListingOwnerKind kind, bool? conflict)
    {
        var context = new ListingReviewContext(Guid.NewGuid(), Guid.NewGuid(),
            new ListingOwnerIdentity(kind, Guid.NewGuid()), null, true, true, null, conflict);
        Assert.Throws<InvalidOperationException>(() => context.EnsureIndependentReviewer());
    }

    [Theory]
    [InlineData(ListingOwnerKind.Individual)]
    [InlineData(ListingOwnerKind.Organization)]
    public void VerifiedIndependentReviewer_CanReviewUnlinkedOwner(ListingOwnerKind kind)
    {
        var context = new ListingReviewContext(Guid.NewGuid(), Guid.NewGuid(),
            new ListingOwnerIdentity(kind, Guid.NewGuid()), null, true, true, null, false);
        context.EnsureIndependentReviewer();
    }

    [Fact]
    public void RegisteredOwner_StillRequiresExplicitConflictDetermination()
    {
        var context = new ListingReviewContext(Guid.NewGuid(), Guid.NewGuid(), Owner(Guid.NewGuid()),
            null, true, true, null, null);
        Assert.Throws<InvalidOperationException>(() => context.EnsureIndependentReviewer());
    }

    private static ListingOwnerIdentity Owner(Guid userId) =>
        new(ListingOwnerKind.Individual, Guid.NewGuid(), userId);
}
