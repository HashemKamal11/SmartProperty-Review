using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingSubmissionTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SubmissionSnapshotsInputsAndDoesNotPublish()
    {
        var (listing, authority, evidence) = Inputs();
        var references = new List<ListingEvidenceReference> { evidence };
        var submission = Draft(listing, authority, references);
        references.Clear();
        var assessments = new List<ListingEvidenceAssessment> { Clean(evidence) };
        submission.Submit(authority.ActingUserId, null, Time.AddMinutes(1), assessments, true, null);
        assessments.Clear();
        Assert.Equal(ListingSubmissionStatus.PendingReview, submission.Status);
        Assert.Equal(Time.AddMinutes(1), submission.SubmittedAt);
        Assert.Equal(1, submission.Version);
        Assert.Null(submission.PredecessorId);
        Assert.Same(evidence, Assert.Single(submission.Evidence));
        Assert.Single(submission.SubmittedAssessments);
        Assert.Equal(ListingPublicationStatus.Unpublished, listing.PublicationStatus);
        Assert.Null(listing.PublishedSubmissionId);
        Assert.Throws<NotSupportedException>(() => ((IList<ListingEvidenceReference>)submission.Evidence).Clear());
        Assert.Throws<InvalidOperationException>(() => submission.Submit(authority.ActingUserId, null, Time.AddMinutes(2), [Clean(evidence)], true, null));
        Assert.Equal(Time.AddMinutes(1), submission.UpdatedAt);
    }

    [Theory]
    [InlineData("legal_unknown")]
    [InlineData("legal_false")]
    [InlineData("actor")]
    [InlineData("workspace")]
    [InlineData("expired")]
    [InlineData("before")]
    [InlineData("scan")]
    [InlineData("future_scan")]
    [InlineData("digest")]
    [InlineData("version")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("null_entry")]
    [InlineData("null_collection")]
    public void FailedSubmissionLeavesEntireSnapshotUnchanged(string fault)
    {
        var (listing, authority, evidence) = Inputs();
        var submission = Draft(listing, authority, [evidence]);
        var at = fault == "before" ? Time.AddSeconds(-1) : fault == "expired" ? Time.AddDays(2) : Time.AddMinutes(1);
        var assessedEvidence = fault is "digest" or "version"
            ? new ListingEvidenceReference(evidence.EvidenceId, fault == "version" ? Guid.NewGuid() : evidence.VersionId,
                evidence.PropertyId, evidence.Purpose, fault == "digest" ? new string('B', 64) : evidence.Sha256) : evidence;
        var assessment = new ListingEvidenceAssessment(Guid.NewGuid(), assessedEvidence,
            fault == "scan" ? (EvidenceScanStatus)0 : EvidenceScanStatus.Clean, fault == "future_scan" ? Time.AddDays(1) : Time);
        IEnumerable<ListingEvidenceAssessment> assessments = fault switch
        {
            "missing" => [], "duplicate" => [assessment, assessment], "null_entry" => [null!],
            "null_collection" => null!, _ => [assessment]
        };
        void Attempt() => submission.Submit(fault == "actor" ? Guid.NewGuid() : authority.ActingUserId,
            fault == "workspace" ? Guid.NewGuid() : null, at, assessments,
            fault == "legal_unknown" ? null : fault != "legal_false", null);
        Assert.NotNull(Record.Exception(Attempt));
        Assert.Equal(ListingSubmissionStatus.Draft, submission.Status);
        Assert.Null(submission.SubmittedAt);
        Assert.Equal(Time, submission.UpdatedAt);
        Assert.Empty(submission.SubmittedAssessments);
        Assert.Same(evidence, Assert.Single(submission.Evidence));
    }


    [Theory]
    [InlineData("empty_id")]
    [InlineData("empty_evidence")]
    [InlineData("duplicate")]
    [InlineData("null_entry")]
    [InlineData("wrong_property")]
    [InlineData("wrong_purpose")]
    [InlineData("early")]
    public void DraftRejectsInvalidIdentityOrEvidence(string fault)
    {
        var (listing, authority, evidence) = Inputs();
        var wrong = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(),
            fault == "wrong_property" ? Guid.NewGuid() : listing.PropertyId,
            fault == "wrong_purpose" ? ListingPurpose.Rent : listing.Purpose, new string('A', 64));
        var references = fault switch
        {
            "empty_evidence" => Array.Empty<ListingEvidenceReference>(), "duplicate" => new[] { evidence, evidence },
            "null_entry" => new ListingEvidenceReference[] { null! },
            "wrong_property" or "wrong_purpose" => new[] { wrong }, _ => new[] { evidence }
        };
        Assert.ThrowsAny<ArgumentException>(() => new ListingSubmission(fault == "empty_id" ? Guid.Empty : Guid.NewGuid(),
            listing, Content(), authority, references, fault == "early" ? Time.AddSeconds(-1) : Time));
    }

    [Fact]
    public void UnreviewedPredecessorCannotAllocateNextVersion()
    {
        var (listing, authority, evidence) = Inputs();
        var previous = Draft(listing, authority, [evidence]);
        Assert.Throws<InvalidOperationException>(() => new ListingSubmission(Guid.NewGuid(), listing, Content(), authority, [evidence], Time, previous));
        Assert.Equal(1, previous.Version);
        Assert.Equal(ListingSubmissionStatus.Draft, previous.Status);
    }

    private static ListingSubmission Draft(PropertyListing listing, ListingLegalAuthority authority, IEnumerable<ListingEvidenceReference> evidence) =>
        new(Guid.NewGuid(), listing, Content(), authority, evidence, Time);
    private static ListingSubmissionContent Content() => new("Country", "Region", "City", "District", "Title", "Description",
        PropertyType.Unit, ListingPurpose.Sale, 10m, "USD", null);
    private static ListingEvidenceAssessment Clean(ListingEvidenceReference evidence) => new(Guid.NewGuid(), evidence, EvidenceScanStatus.Clean, Time);
    private static (PropertyListing, ListingLegalAuthority, ListingEvidenceReference) Inputs()
    {
        var listing = new PropertyListing(Guid.NewGuid(), Guid.NewGuid(), ListingPurpose.Sale, Time);
        var actor = Guid.NewGuid();
        var owner = new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), actor);
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose, owner, actor,
            ListingRepresentationKind.Owner, null, Time, Time.AddDays(1));
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId, listing.Purpose, new string('A', 64));
        return (listing, authority, evidence);
    }
}
