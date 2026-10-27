using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingWriteDomainTests
{
    private static readonly DateTimeOffset At = MarketplaceFixtures.CreatedAt;
    private static ListingSubmission Draft(PropertyListing? listing = null, ListingSubmission? predecessor = null)
    {
        listing ??= new PropertyListing(Guid.NewGuid(), Guid.NewGuid(), ListingPurpose.Sale, At);
        var actor = Guid.NewGuid();
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), actor), actor,
            ListingRepresentationKind.Owner, null, At, At.AddDays(1));
        return new ListingSubmission(Guid.NewGuid(), listing, Content(), authority, [], At.AddHours(2), predecessor);
    }

    private static ListingSubmissionContent Content(ListingPurpose purpose = ListingPurpose.Sale) =>
        new("EG", "Region", "City", "District", "Title", "Description", PropertyType.Unit,
            purpose, 100m, "EGP", purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);

    [Fact]
    public void EmptyDraftIsDiscardableButCannotSubmitEmptyAssessments()
    {
        var draft = Draft();
        draft.EnsureCanDiscard(1);
        Assert.Throws<ArgumentException>(() => draft.Submit(1, draft.Authority.ActingUserId, null,
            At.AddHours(3), [], true, null));
        Assert.Equal(1, draft.Revision);
        Assert.Equal(ListingSubmissionStatus.Draft, draft.Status);
        Assert.Null(draft.SubmittedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public void StaleWritesFailWithoutMutation(int revision)
    {
        var draft = Draft();
        var original = draft.Content;
        Assert.Throws<InvalidOperationException>(() => draft.UpdateDraft(revision, Content(), [], At.AddHours(3)));
        Assert.Throws<InvalidOperationException>(() => draft.EnsureCanDiscard(revision));
        Assert.Throws<InvalidOperationException>(() => draft.Submit(revision, draft.Authority.ActingUserId, null, At.AddHours(3), [], true, null));
        Assert.Same(original, draft.Content);
        Assert.Equal(1, draft.Revision);
        Assert.Equal(At.AddHours(2), draft.UpdatedAt);
    }

    [Fact]
    public void UpdateAndSubmitAdvanceRevisionAndFreezeSubmittedSnapshot()
    {
        var draft = Draft();
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), draft.PropertyId, draft.Purpose, new string('A', 64));
        var references = new List<ListingEvidenceReference> { evidence };
        draft.UpdateDraft(1, Content(), references, At.AddHours(3));
        references.Clear();
        Assert.Single(draft.Evidence);
        Assert.Equal(2, draft.Revision);
        Assert.Equal(1, draft.Version);
        Assert.Throws<InvalidOperationException>(() => draft.Submit(1, draft.Authority.ActingUserId, null, At.AddHours(3), [], true, null));
        draft.Submit(2, draft.Authority.ActingUserId, null, At.AddHours(3),
            [new ListingEvidenceAssessment(Guid.NewGuid(), evidence, EvidenceScanStatus.Clean, At.AddHours(3))], true, null);
        Assert.Equal(3, draft.Revision);
        Assert.Throws<InvalidOperationException>(() => draft.UpdateDraft(3, Content(), [], At.AddHours(4)));
        Assert.Throws<InvalidOperationException>(() => draft.EnsureCanDiscard(3));
        Assert.Single(draft.Evidence);
        Assert.Single(draft.SubmittedAssessments);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("property")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("time")]
    public void InvalidUpdatePreservesAllDraftValues(string fault)
    {
        var draft = Draft();
        var before = draft.Content;
        var reference = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(),
            fault == "property" ? Guid.NewGuid() : draft.PropertyId, draft.Purpose, new string('A', 64));
        ListingEvidenceReference[] evidence = fault switch { "duplicate" => [reference, reference], "null" => [null!], _ => [reference] };
        Assert.Throws<ArgumentException>(() => draft.UpdateDraft(1,
            Content(fault == "purpose" ? ListingPurpose.Rent : ListingPurpose.Sale), evidence,
            fault == "time" ? At : At.AddHours(3)));
        Assert.Same(before, draft.Content);
        Assert.Empty(draft.Evidence);
        Assert.Equal(1, draft.Revision);
        Assert.Equal(At.AddHours(2), draft.UpdatedAt);
    }

    [Fact]
    public void EditingAndDiscardEligibilityNeverChangePublishedPredecessor()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var first = MarketplaceFixtures.Approve(listing, At.AddHours(1));
        var draft = Draft(listing, first);
        var decision = first.ReviewDecision;
        draft.UpdateDraft(1, Content(), [], At.AddHours(3));
        draft.EnsureCanDiscard(2);
        Assert.Equal(first.Id, listing.PublishedSubmissionId);
        Assert.Equal(ListingPublicationStatus.Published, listing.PublicationStatus);
        Assert.Equal(ListingSubmissionStatus.Approved, first.Status);
        Assert.Same(decision, first.ReviewDecision);
        Assert.Equal(2, draft.Version);
        Assert.Equal(first.Id, draft.PredecessorId);
    }
}
