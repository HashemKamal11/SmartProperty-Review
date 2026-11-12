using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class AuthorityRequestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Legal = Guid.NewGuid();
    private static readonly Guid Issuer = Guid.NewGuid();
    private static readonly AuthorityRepresentative Broker = new("BL-778", "Nile Realty", "Ahmed Hassan");
    private static readonly string Digest = new('A', 64);

    [Fact]
    public void ARequestNeedsAtLeastOnePurposeAndStartsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new AuthorityRequest(Guid.NewGuid(), Guid.NewGuid(), Requester, Guid.NewGuid(), false, false, T0));

        var request = New(sale: true, rent: true);

        Assert.Equal([ListingPurpose.Sale, ListingPurpose.Rent], request.Purposes);
        Assert.Equal(["Details", "SaleListing", "RentListing", "OwnershipProof"], request.Missing);
    }

    [Fact]
    public void ADocumentCountsOnlyOnceItIsScannedClean()
    {
        var request = New();
        request.UpdateDetails(ListingRepresentationKind.Owner, " D-9 ", Broker, T0);
        request.SetListingContent(ListingPurpose.Sale, "{\"title\":\"x\"}", T0);

        var deed = Version(request, AuthorityDocumentType.TitleDeed);
        request.AddDocument(deed, T0);

        Assert.Equal(["DocumentScanPending"], request.Missing);
        Assert.Throws<InvalidOperationException>(() => request.Submit(T0));

        deed.MarkClean("ClamAV 1.4/1", CleanKey(deed), T0.AddSeconds(5));
        request.Submit(T0.AddMinutes(1));

        Assert.Equal(AuthorityRequestStatus.PendingLegalReview, request.Status);
        Assert.Equal("D-9", request.OwnershipDocumentNumber);
        Assert.Null(request.Representative);
    }

    [Fact]
    public void AnInfectedOrUnscannableDocumentBlocksSubmission()
    {
        var request = Complete(submit: false);
        var extra = Version(request, AuthorityDocumentType.UtilityBill);
        request.AddDocument(extra, T0);

        extra.MarkInfected("ClamAV", "Eicar-Signature", T0.AddSeconds(1));

        Assert.Equal(["DocumentNotClean"], request.Missing);
        Assert.Equal(DocumentScanStatus.Rejected, extra.ScanStatus);
        Assert.False(extra.IsAvailable);
    }

    [Fact]
    public void ScanErrorsAreRetriedThenFailAndARescanStartsOver()
    {
        var request = New();
        var document = Version(request, AuthorityDocumentType.TitleDeed);

        document.RecordScanError("timeout", maxAttempts: 2, retryAt: T0.AddMinutes(1), at: T0);
        Assert.Equal(DocumentScanStatus.PendingScan, document.ScanStatus);
        Assert.Equal(T0.AddMinutes(1), document.NextScanAt);

        document.RecordScanError("timeout", maxAttempts: 2, retryAt: T0.AddMinutes(2), at: T0.AddMinutes(1));
        Assert.Equal(DocumentScanStatus.ScanFailed, document.ScanStatus);
        Assert.Null(document.NextScanAt);
        Assert.Throws<InvalidOperationException>(() => document.MarkClean("x", "documents/a.pdf", T0));

        document.RequestRescan(T0.AddMinutes(3));
        Assert.Equal(DocumentScanStatus.PendingScan, document.ScanStatus);
        Assert.Equal(0, document.ScanAttempts);
        Assert.Throws<InvalidOperationException>(() => document.RequestRescan(T0.AddMinutes(4)));
    }

    [Fact]
    public void ARepresentativeAlsoNeedsTheAuthorizationAndTheBrokerageLicense()
    {
        var request = Complete(ListingRepresentationKind.IndividualRepresentative, submit: false);

        Assert.Equal(["Authorization", "BrokerageLicense"], request.Missing);

        AddClean(request, AuthorityDocumentType.Authorization);
        AddClean(request, AuthorityDocumentType.BrokerageLicense);
        request.Submit(T0.AddMinutes(1));

        Assert.Equal(AuthorityRequestStatus.PendingLegalReview, request.Status);
    }

    [Theory]
    [InlineData(null, "Agency", "Owner")]
    [InlineData("BL", " ", "Owner")]
    [InlineData("BL", "Agency", "")]
    public void ARepresentativeMustGiveLicenseAgencyAndOwner(string? license, string agency, string owner)
    {
        var request = New();

        Assert.Throws<ArgumentException>(() => request.UpdateDetails(
            ListingRepresentationKind.IndividualRepresentative, "D-1", new AuthorityRepresentative(license!, agency, owner), T0));
        Assert.Throws<ArgumentException>(() => request.UpdateDetails(ListingRepresentationKind.IndividualRepresentative, "D-1", null, T0));
        Assert.Throws<ArgumentException>(() => request.UpdateDetails(ListingRepresentationKind.WorkspaceRepresentative, "D-1", null, T0));
        Assert.Null(request.Capacity);
    }

    [Fact]
    public void ReplacingKeepsEveryVersionAndOnlyTheNewOneIsInForce()
    {
        var request = Complete(submit: false);
        var first = request.CurrentDocuments.Single();
        var second = Version(request, first.Type, first.DocumentId, 2);

        var superseded = request.ReplaceDocument(second, T0.AddMinutes(5));

        Assert.Same(first, superseded);
        Assert.NotNull(first.SupersededAt);
        Assert.Equal([1, 2], request.VersionsOf(first.DocumentId).Select(version => version.VersionNumber));
        Assert.Same(second, request.CurrentVersionOf(first.DocumentId));
        Assert.Equal(["DocumentScanPending"], request.Missing);

        // Not the next number, or another type: refused.
        Assert.Throws<ArgumentException>(() => request.ReplaceDocument(Version(request, first.Type, first.DocumentId, 2), T0));
        Assert.Throws<ArgumentException>(() => request.ReplaceDocument(Version(request, AuthorityDocumentType.Other, first.DocumentId, 3), T0));
    }

    [Fact]
    public void RemovingKeepsTheRecordButADocumentInAnApprovalCannotBeRemoved()
    {
        var request = Complete(submit: false);
        var bill = AddClean(request, AuthorityDocumentType.UtilityBill);

        Assert.Same(bill, request.RemoveDocument(bill.DocumentId, T0.AddMinutes(1)));
        Assert.NotNull(bill.RemovedAt);
        Assert.Contains(bill, request.Documents);
        Assert.DoesNotContain(bill, request.CurrentDocuments);

        var deed = request.CurrentDocuments.Single();
        request.Submit(T0.AddMinutes(2));
        request.ApproveLegalReview(Legal, null, T0, attestedNoConflict: true, T0.AddHours(1));
        request.RequestCompletion(Issuer, "Add a utility bill.", attestedNoConflict: true, T0.AddHours(2));

        Assert.True(request.WasApproved(deed.DocumentId));
        Assert.Throws<InvalidOperationException>(() => request.RemoveDocument(deed.DocumentId, T0.AddHours(3)));
    }

    [Fact]
    public void EverythingIsFrozenOnceSubmitted()
    {
        var request = Complete();
        var deed = request.CurrentDocuments.Single();

        Assert.Throws<InvalidOperationException>(() => request.AddDocument(Version(request, AuthorityDocumentType.Other), T0));
        Assert.Throws<InvalidOperationException>(() => request.RemoveDocument(deed.DocumentId, T0));
        Assert.Throws<InvalidOperationException>(() => request.ReplaceDocument(Version(request, deed.Type, deed.DocumentId, 2), T0));
        Assert.Throws<InvalidOperationException>(() => request.UpdateDetails(ListingRepresentationKind.Owner, "D-2", null, T0));
        Assert.Throws<InvalidOperationException>(() => request.SetListingContent(ListingPurpose.Sale, "{}", T0));
    }

    [Fact]
    public void TheHappyPathRecordsEveryDecisionWithTheDocumentVersions()
    {
        var request = Complete();
        var deed = request.CurrentDocuments.Single();
        var saleGrant = Guid.NewGuid();

        request.ApproveLegalReview(Legal, "  ok  ", T0.AddMinutes(30), attestedNoConflict: true, T0.AddHours(1));
        request.MarkIssued(Issuer, T0.AddDays(365), saleGrant, null, attestedNoConflict: true, T0.AddHours(2));

        Assert.Equal(AuthorityRequestStatus.Approved, request.Status);
        Assert.Equal("ok", request.LegalReviewNote);
        Assert.Equal(T0.AddMinutes(30), request.ReviewedPropertyUpdatedAt);
        Assert.Equal(
            [AuthorityDecisionKind.Submitted, AuthorityDecisionKind.LegalApproved, AuthorityDecisionKind.AdminApproved],
            request.Decisions.Select(decision => decision.Kind));
        Assert.All(request.Decisions, decision => Assert.Equal([deed.Id], decision.DocumentVersionIds));
        Assert.Equal([Requester, Legal, Issuer], request.Decisions.Select(decision => decision.ActorUserId));
        Assert.True(request.Decisions[2].AttestedNoConflict);
        Assert.False(request.IsOpen);
    }

    [Fact]
    public void NobodyDecidesWithoutConfirmingNoConflictAndTheRequesterAndLegalReviewerAreKeptApart()
    {
        var request = Complete();

        Assert.Throws<InvalidOperationException>(() => request.ApproveLegalReview(Legal, null, T0, attestedNoConflict: false, T0.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => request.ApproveLegalReview(Requester, null, T0, attestedNoConflict: true, T0.AddHours(1)));

        request.ApproveLegalReview(Legal, null, T0, attestedNoConflict: true, T0.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => request.MarkIssued(Legal, T0.AddDays(10), Guid.NewGuid(), null, true, T0.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => request.Reject(Legal, "no", true, T0.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => request.RequestCompletion(Legal, "no", true, T0.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => request.MarkIssued(Requester, T0.AddDays(10), Guid.NewGuid(), null, true, T0.AddHours(2)));
        Assert.Throws<ArgumentException>(() => request.MarkIssued(Issuer, T0.AddDays(10), null, Guid.NewGuid(), true, T0.AddHours(2)));
    }

    [Fact]
    public void ACompletionRequestMakesItADraftAgainAndBothReviewsStartOver()
    {
        var request = Complete();
        request.ApproveLegalReview(Legal, "fine", T0, attestedNoConflict: true, T0.AddHours(1));

        Assert.Throws<ArgumentException>(() => request.RequestCompletion(Issuer, "  ", true, T0.AddHours(2)));

        request.RequestCompletion(Issuer, "Price looks wrong.", true, T0.AddHours(2));

        Assert.Equal(AuthorityRequestStatus.Draft, request.Status);
        Assert.Null(request.SubmittedAt);
        Assert.Null(request.LegalReviewedByUserId);
        Assert.Null(request.ReviewedPropertyUpdatedAt);
        Assert.Equal("Price looks wrong.", request.CompletionRequestReason);
        Assert.Equal(AuthorityRequestStatus.PendingIssuance, request.CompletionRequestedAtStage);
        Assert.Null(request.RejectionReason);

        request.SetListingContent(ListingPurpose.Sale, "{\"title\":\"fixed\"}", T0.AddHours(3));
        request.Submit(T0.AddHours(4));
        Assert.Equal(AuthorityRequestStatus.PendingLegalReview, request.Status);
        Assert.Equal(AuthorityDecisionKind.AdminCompletionRequested, request.Decisions[2].Kind);
    }

    [Fact]
    public void ARejectionIsFinalAndKeepsTheReason()
    {
        var request = Complete();

        request.Reject(Legal, "The deed belongs to someone else.", true, T0.AddHours(1));

        Assert.Equal(AuthorityRequestStatus.Rejected, request.Status);
        Assert.Equal(AuthorityRequestStatus.PendingLegalReview, request.RejectedAtStage);
        Assert.False(request.IsOpen);
        Assert.Throws<InvalidOperationException>(() => request.Withdraw(T0.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => request.Submit(T0.AddHours(2)));
        Assert.Equal(AuthorityDecisionKind.LegalRejected, request.Decisions[^1].Kind);
    }

    [Fact]
    public void OnlyAnOpenRequestCanBeWithdrawnAndEveryChangeMovesTheVersion()
    {
        var request = Complete();
        var before = request.Version;

        request.Withdraw(T0.AddHours(1));

        Assert.True(request.Version > before);
        Assert.Equal(AuthorityDecisionKind.Withdrawn, request.Decisions[^1].Kind);
        Assert.Throws<InvalidOperationException>(() => request.Withdraw(T0.AddHours(2)));
    }

    [Fact]
    public void ADocumentKeepsOnlyTheFileNameAndNeedsARealDigest()
    {
        var request = New();
        var document = new AuthorityRequestDocument(
            Guid.NewGuid(), request.Id, Guid.NewGuid(), 1, AuthorityDocumentType.TitleDeed, "quarantine/k.pdf",
            AuthorityRequestDocument.Pdf, "..\\..\\etc/deed.pdf", 10, Digest.ToLowerInvariant(), Requester, T0);

        Assert.Equal("deed.pdf", document.FileName);
        Assert.Equal(Digest, document.Sha256);
        Assert.Equal(DocumentScanStatus.PendingScan, document.ScanStatus);
        Assert.Throws<ArgumentException>(() => new AuthorityRequestDocument(
            Guid.NewGuid(), request.Id, Guid.NewGuid(), 1, AuthorityDocumentType.TitleDeed, "k", "text/html", "x.html", 10, Digest, Requester, T0));
        Assert.Throws<ArgumentException>(() => new AuthorityRequestDocument(
            Guid.NewGuid(), request.Id, Guid.NewGuid(), 1, AuthorityDocumentType.TitleDeed, "k", AuthorityRequestDocument.Pdf, "x.pdf", 10, "abc", Requester, T0));
    }

    [Fact]
    public void ADraftHoldsAtMostTenDocumentsInForce()
    {
        var request = New();

        for (var i = 0; i < AuthorityRequest.MaxDocuments; i++)
        {
            request.AddDocument(Version(request, AuthorityDocumentType.Other), T0);
        }

        Assert.Throws<InvalidOperationException>(() => request.AddDocument(Version(request, AuthorityDocumentType.Other), T0));

        request.RemoveDocument(request.CurrentDocuments[0].DocumentId, T0);
        request.AddDocument(Version(request, AuthorityDocumentType.Other), T0);
        Assert.Equal(AuthorityRequest.MaxDocuments, request.CurrentDocuments.Count);
    }

    private static AuthorityRequest New(bool sale = true, bool rent = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Requester, Guid.NewGuid(), sale, rent, T0);

    /// <summary>A request with everything an owner needs, its deed scanned clean (a representative lacks their two extra documents).</summary>
    private static AuthorityRequest Complete(
        ListingRepresentationKind capacity = ListingRepresentationKind.Owner, bool submit = true, bool sale = true, bool rent = false)
    {
        var request = New(sale, rent);
        request.UpdateDetails(capacity, "D-12345", capacity == ListingRepresentationKind.Owner ? null : Broker, T0);

        foreach (var purpose in request.Purposes)
        {
            request.SetListingContent(purpose, "{\"title\":\"x\"}", T0);
        }

        AddClean(request, AuthorityDocumentType.TitleDeed);

        if (submit)
        {
            request.Submit(T0.AddMinutes(1));
        }

        return request;
    }

    private static AuthorityRequestDocument AddClean(AuthorityRequest request, AuthorityDocumentType type)
    {
        var version = Version(request, type);
        request.AddDocument(version, T0);
        version.MarkClean("ClamAV", CleanKey(version), T0.AddSeconds(1));

        return version;
    }

    private static string CleanKey(AuthorityRequestDocument version) => "documents/" + version.StorageKey["quarantine/".Length..];

    private static AuthorityRequestDocument Version(
        AuthorityRequest request, AuthorityDocumentType type, Guid? documentId = null, int number = 1)
    {
        var id = Guid.NewGuid();

        return new(id, request.Id, documentId ?? Guid.NewGuid(), number, type, $"quarantine/{request.Id:N}/{id:N}.pdf",
            AuthorityRequestDocument.Pdf, "doc.pdf", 100, Digest, Requester, T0);
    }
}
