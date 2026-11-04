using SmartProperty.Domain.PropertyRegistration;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;
using static SmartProperty.UnitTests.Domain.PropertyRegistration.PropertyRegistrationFixtures;

namespace SmartProperty.UnitTests.Domain.PropertyRegistration;

/// <summary>
/// Covers construction, draft editing, and submission and resubmission of the registration request.
/// </summary>
public sealed class PropertyRegistrationRequestTests
{
    [Fact]
    public void Constructor_StartsAsADraftAttributedToTheCreator()
    {
        var id = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var details = CompleteUnit();

        var request = new PropertyRegistrationRequest(id, "  PRR-7  ", workspaceId, Creator, details, CreatedAt);

        Assert.Equal(id, request.Id);
        Assert.Equal("PRR-7", request.BusinessReference);
        Assert.Equal(workspaceId, request.WorkspaceId);
        Assert.Equal(Creator, request.CreatedByUserId);
        Assert.Equal(Creator, request.LastModifiedByUserId);
        Assert.Equal(PropertyRegistrationRequestStatus.Draft, request.Status);
        Assert.Same(details, request.Details);
        Assert.Equal(CreatedAt, request.CreatedAt);
        Assert.Equal(CreatedAt, request.UpdatedAt);
        Assert.Equal(0, request.CurrentVersion);
        Assert.Null(request.ResolvedPropertyId);
        Assert.Null(request.Withdrawal);
        Assert.Empty(request.Submissions);
        Assert.Empty(request.Reviews);
    }

    [Fact]
    public void Constructor_AcceptsAnIncompleteDraft()
    {
        var request = CreateDraft(new PropertyRegistrationRequestDetails());

        Assert.Equal(PropertyRegistrationRequestStatus.Draft, request.Status);
    }

    [Fact]
    public void Constructor_RejectsEmptyIdentifiers()
    {
        var details = CompleteUnit();

        Assert.Throws<ArgumentException>(() => new PropertyRegistrationRequest(Guid.Empty, "R", Guid.NewGuid(), Creator, details, CreatedAt));
        Assert.Throws<ArgumentException>(() => new PropertyRegistrationRequest(Guid.NewGuid(), "R", Guid.Empty, Creator, details, CreatedAt));
        Assert.Throws<ArgumentException>(() => new PropertyRegistrationRequest(Guid.NewGuid(), "R", Guid.NewGuid(), Guid.Empty, details, CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankBusinessReference(string reference)
    {
        Assert.Throws<ArgumentException>(
            () => new PropertyRegistrationRequest(Guid.NewGuid(), reference, Guid.NewGuid(), Creator, CompleteUnit(), CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsNullDetails()
    {
        Assert.Throws<ArgumentNullException>(
            () => new PropertyRegistrationRequest(Guid.NewGuid(), "R", Guid.NewGuid(), Creator, null!, CreatedAt));
    }

    [Fact]
    public void UpdateDetails_InADraft_ReplacesDetailsAndRecordsTheActor()
    {
        var request = CreateDraft();
        var updated = CompleteUnit(bedrooms: 4);

        var changed = request.UpdateDetails(updated, Editor, At(1));

        Assert.True(changed);
        Assert.Same(updated, request.Details);
        Assert.Equal(Editor, request.LastModifiedByUserId);
        Assert.Equal(At(1), request.UpdatedAt);
        Assert.Equal(Creator, request.CreatedByUserId);
        Assert.Equal(PropertyRegistrationRequestStatus.Draft, request.Status);
    }

    [Fact]
    public void UpdateDetails_WhenChangesAreRequired_IsAllowedAndKeepsTheStatus()
    {
        var request = CreateChangesRequired();

        var changed = request.UpdateDetails(CompleteUnit(bedrooms: 5), Editor, At(3));

        Assert.True(changed);
        Assert.Equal(PropertyRegistrationRequestStatus.ChangesRequired, request.Status);
    }

    [Fact]
    public void UpdateDetails_WithIdenticalValuesInASeparateInstance_ReturnsFalseAndChangesNothing()
    {
        var request = CreateDraft();
        var original = request.Details;
        var identical = CompleteUnit();
        Assert.NotSame(original, identical);

        var changed = request.UpdateDetails(identical, Editor, At(5));

        Assert.False(changed);
        Assert.Same(original, request.Details);
        Assert.Equal(Creator, request.LastModifiedByUserId);
        Assert.Equal(CreatedAt, request.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_WithIdenticalValuesAndAnEarlierClock_IsStillANoOp()
    {
        var request = CreateDraft();

        var changed = request.UpdateDetails(CompleteUnit(), Editor, CreatedAt.AddHours(-5));

        Assert.False(changed);
    }

    [Fact]
    public void UpdateDetails_RejectsANoOpWhenTheLifecycleForbidsEditing()
    {
        var request = CreateUnderReview();

        Assert.Throws<InvalidOperationException>(() => request.UpdateDetails(CompleteUnit(), Editor, At(5)));
    }

    [Theory]
    [MemberData(nameof(NonEditableRequests))]
    public void UpdateDetails_RejectedByState_LeavesTheRequestUnchanged(Func<PropertyRegistrationRequest> create)
    {
        var request = create();
        var details = request.Details;
        var updatedAt = request.UpdatedAt;
        var modifiedBy = request.LastModifiedByUserId;

        Assert.Throws<InvalidOperationException>(() => request.UpdateDetails(CompleteUnit(bedrooms: 9), Editor, At(10)));

        Assert.Same(details, request.Details);
        Assert.Equal(updatedAt, request.UpdatedAt);
        Assert.Equal(modifiedBy, request.LastModifiedByUserId);
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> NonEditableRequests() =>
        new()
        {
            CreateUnderReview,
            CreateApproved,
            CreateRejected,
            CreateWithdrawn
        };

    [Fact]
    public void UpdateDetails_RejectsAnEmptyActorAndNullDetailsWithoutMutating()
    {
        var request = CreateDraft();
        var details = request.Details;

        Assert.Throws<ArgumentException>(() => request.UpdateDetails(CompleteUnit(bedrooms: 4), Guid.Empty, At(1)));
        Assert.Throws<ArgumentNullException>(() => request.UpdateDetails(null!, Editor, At(1)));

        Assert.Same(details, request.Details);
        Assert.Equal(CreatedAt, request.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_RejectsATimestampBeforeTheLastChangeWithoutMutating()
    {
        var request = CreateDraft();
        request.UpdateDetails(CompleteUnit(bedrooms: 4), Editor, At(3));
        var details = request.Details;

        Assert.Throws<ArgumentException>(() => request.UpdateDetails(CompleteUnit(bedrooms: 5), Creator, At(2)));

        Assert.Same(details, request.Details);
        Assert.Equal(At(3), request.UpdatedAt);
        Assert.Equal(Editor, request.LastModifiedByUserId);
    }

    [Fact]
    public void Submit_CreatesVersionOneWithTheSubmittedContent()
    {
        var request = CreateDraft();
        var submissionId = Guid.NewGuid();

        request.Submit(submissionId, Editor, At(1));

        var submission = Assert.Single(request.Submissions);
        Assert.Equal(submissionId, submission.Id);
        Assert.Equal(1, submission.VersionNumber);
        Assert.Equal(Editor, submission.SubmittedByUserId);
        Assert.Equal(At(1), submission.SubmittedAt);
        Assert.Equal(request.Details, submission.Details);
        Assert.Equal(1, request.CurrentVersion);
        Assert.Equal(PropertyRegistrationRequestStatus.UnderReview, request.Status);
        Assert.Equal(Editor, request.LastModifiedByUserId);
        Assert.Equal(At(1), request.UpdatedAt);
    }

    [Fact]
    public void Submit_WithAnEmptyDraft_IsRejectedNamingTheMissingFieldsWithoutMutating()
    {
        var request = CreateDraft(new PropertyRegistrationRequestDetails(PropertyType.Unit));

        var exception = Assert.Throws<InvalidOperationException>(() => request.Submit(Guid.NewGuid(), Creator, At(1)));

        Assert.Contains("Address", exception.Message);
        Assert.Contains("Area", exception.Message);
        Assert.Contains("Bedrooms", exception.Message);
        Assert.Equal(PropertyRegistrationRequestStatus.Draft, request.Status);
        Assert.Empty(request.Submissions);
        Assert.Equal(CreatedAt, request.UpdatedAt);
    }

    [Fact]
    public void Submit_WithMissingAddressComponents_IsRejectedNamingThemWithoutMutating()
    {
        var details = new PropertyRegistrationRequestDetails(PropertyType.Building, new PropertyAddress("SA"), 800m);
        var request = CreateDraft(details);

        var exception = Assert.Throws<InvalidOperationException>(() => request.Submit(Guid.NewGuid(), Creator, At(1)));

        Assert.Contains("Address.City", exception.Message);
        Assert.Contains("Address.District", exception.Message);
        Assert.Contains("Address.AddressLine", exception.Message);
        Assert.Equal(PropertyRegistrationRequestStatus.Draft, request.Status);
        Assert.Empty(request.Submissions);
    }

    [Fact]
    public void Submit_RejectsEmptyIdsAndEarlierTimestampsWithoutMutating()
    {
        var request = CreateDraft();

        Assert.Throws<ArgumentException>(() => request.Submit(Guid.Empty, Creator, At(1)));
        Assert.Throws<ArgumentException>(() => request.Submit(Guid.NewGuid(), Guid.Empty, At(1)));
        Assert.Throws<ArgumentException>(() => request.Submit(Guid.NewGuid(), Creator, CreatedAt.AddMinutes(-1)));

        Assert.Equal(PropertyRegistrationRequestStatus.Draft, request.Status);
        Assert.Empty(request.Submissions);
        Assert.Equal(CreatedAt, request.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(NonDraftRequests))]
    public void Submit_FromAnyStateOtherThanDraft_IsRejectedWithoutMutating(Func<PropertyRegistrationRequest> create)
    {
        var request = create();
        var status = request.Status;
        var versions = request.CurrentVersion;

        Assert.Throws<InvalidOperationException>(() => request.Submit(Guid.NewGuid(), Creator, At(10)));

        Assert.Equal(status, request.Status);
        Assert.Equal(versions, request.CurrentVersion);
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> NonDraftRequests() =>
        new()
        {
            CreateUnderReview,
            CreateChangesRequired,
            CreateApproved,
            CreateRejected,
            CreateWithdrawn
        };

    [Fact]
    public void Resubmit_RejectsAReusedSubmissionId()
    {
        var request = CreateDraft();
        var submissionId = Guid.NewGuid();
        request.Submit(submissionId, Creator, At(1));
        request.RequestChanges(Guid.NewGuid(), Reviewer, "Fix.", At(2));

        Assert.Throws<ArgumentException>(() => request.Resubmit(submissionId, Creator, At(3)));

        Assert.Equal(PropertyRegistrationRequestStatus.ChangesRequired, request.Status);
        Assert.Single(request.Submissions);
    }

    [Fact]
    public void Resubmit_CreatesTheNextVersionAndKeepsIdentityAndReference()
    {
        var request = CreateChangesRequired();
        var id = request.Id;
        var reference = request.BusinessReference;
        var firstSubmission = request.Submissions[0];
        request.UpdateDetails(CompleteUnit(bedrooms: 6), Editor, At(3));
        var resubmissionId = Guid.NewGuid();

        request.Resubmit(resubmissionId, Editor, At(4));

        Assert.Equal(2, request.Submissions.Count);
        Assert.Equal(2, request.Submissions[1].VersionNumber);
        Assert.Equal(resubmissionId, request.Submissions[1].Id);
        Assert.Equal(6, request.Submissions[1].Details.Bedrooms);
        Assert.Equal(2, request.CurrentVersion);
        Assert.Equal(PropertyRegistrationRequestStatus.UnderReview, request.Status);
        Assert.Equal(id, request.Id);
        Assert.Equal(reference, request.BusinessReference);
        Assert.Same(firstSubmission, request.Submissions[0]);
    }

    [Fact]
    public void Resubmit_WithIncompleteDetails_IsRejectedWithoutMutating()
    {
        var request = CreateChangesRequired();
        request.UpdateDetails(new PropertyRegistrationRequestDetails(PropertyType.Unit), Editor, At(3));

        Assert.Throws<InvalidOperationException>(() => request.Resubmit(Guid.NewGuid(), Editor, At(4)));

        Assert.Equal(PropertyRegistrationRequestStatus.ChangesRequired, request.Status);
        Assert.Single(request.Submissions);
        Assert.Equal(At(3), request.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(NonChangesRequiredRequests))]
    public void Resubmit_FromAnyStateOtherThanChangesRequired_IsRejected(Func<PropertyRegistrationRequest> create)
    {
        var request = create();
        var versions = request.CurrentVersion;

        Assert.Throws<InvalidOperationException>(() => request.Resubmit(Guid.NewGuid(), Creator, At(10)));

        Assert.Equal(versions, request.CurrentVersion);
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> NonChangesRequiredRequests() =>
        new()
        {
            () => CreateDraft(),
            CreateUnderReview,
            CreateApproved,
            CreateRejected,
            CreateWithdrawn
        };
}
