using SmartProperty.Domain.PropertyRegistration;
using Xunit;
using static SmartProperty.UnitTests.Domain.PropertyRegistration.PropertyRegistrationFixtures;

namespace SmartProperty.UnitTests.Domain.PropertyRegistration;

/// <summary>
/// Covers reviewer decisions, withdrawal, and the terminal states. Every rejected call is checked to leave the
/// request untouched.
/// </summary>
public sealed class PropertyRegistrationRequestDecisionTests
{
    private delegate void Decision(PropertyRegistrationRequest request, Guid reviewId, string reason, DateTimeOffset at);

    private static readonly Decision RequestChanges = (r, id, reason, at) => r.RequestChanges(id, Reviewer, reason, at);
    private static readonly Decision Reject = (r, id, reason, at) => r.Reject(id, Reviewer, reason, at);
    private static readonly Decision Approve = (r, id, reason, at) => r.Approve(id, Reviewer, Guid.NewGuid(), reason, at);

    public static TheoryData<string, PropertyRegistrationRequestStatus, PropertyRegistrationDecision> Outcomes() =>
        new()
        {
            { nameof(RequestChanges), PropertyRegistrationRequestStatus.ChangesRequired, PropertyRegistrationDecision.ChangesRequested },
            { nameof(Reject), PropertyRegistrationRequestStatus.Rejected, PropertyRegistrationDecision.Rejected },
            { nameof(Approve), PropertyRegistrationRequestStatus.Approved, PropertyRegistrationDecision.Approved }
        };

    private static Decision Pick(string name) => name switch
    {
        nameof(RequestChanges) => RequestChanges,
        nameof(Reject) => Reject,
        _ => Approve
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Decision_RecordsReviewerReasonTimestampAndExactSubmission(
        string decision,
        PropertyRegistrationRequestStatus expectedStatus,
        PropertyRegistrationDecision expectedDecision)
    {
        var request = CreateUnderReview();
        var submission = request.Submissions[0];
        var reviewId = Guid.NewGuid();

        Pick(decision)(request, reviewId, "  Because.  ", At(2));

        var review = Assert.Single(request.Reviews);
        Assert.Equal(reviewId, review.Id);
        Assert.Equal(expectedDecision, review.Decision);
        Assert.Equal(Reviewer, review.ReviewerUserId);
        Assert.Equal("Because.", review.Reason);
        Assert.Equal(At(2), review.DecidedAt);
        Assert.Equal(submission.Id, review.SubmissionId);
        Assert.Equal(1, review.SubmissionVersion);
        Assert.Equal(expectedStatus, request.Status);
        Assert.Equal(Reviewer, request.LastModifiedByUserId);
        Assert.Equal(At(2), request.UpdatedAt);
    }

    [Fact]
    public void Approve_RecordsTheResolvedPropertyOnTheRequestAndTheReview()
    {
        var request = CreateUnderReview();
        var propertyId = Guid.NewGuid();

        request.Approve(Guid.NewGuid(), Reviewer, propertyId, "Verified.", At(2));

        Assert.Equal(propertyId, request.ResolvedPropertyId);
        Assert.Equal(propertyId, request.Reviews[0].ResolvedPropertyId);
    }

    [Fact]
    public void NonApprovals_LeaveTheResolvedPropertyEmpty()
    {
        var changes = CreateChangesRequired();
        var rejected = CreateRejected();

        Assert.Null(changes.ResolvedPropertyId);
        Assert.Null(changes.Reviews[0].ResolvedPropertyId);
        Assert.Null(rejected.ResolvedPropertyId);
    }

    [Fact]
    public void Approve_RejectsAnEmptyResolvedPropertyWithoutMutating()
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => request.Approve(Guid.NewGuid(), Reviewer, Guid.Empty, "Ok.", At(2)));

        AssertUntouchedUnderReview(request);
    }

    [Theory]
    [InlineData("RequestChanges")]
    [InlineData("Reject")]
    [InlineData("Approve")]
    public void Decision_RejectsABlankReasonWithoutMutating(string decision)
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => Pick(decision)(request, Guid.NewGuid(), "   ", At(2)));

        AssertUntouchedUnderReview(request);
    }

    [Theory]
    [InlineData("RequestChanges")]
    [InlineData("Reject")]
    [InlineData("Approve")]
    public void Decision_RejectsEmptyIdsWithoutMutating(string decision)
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => Pick(decision)(request, Guid.Empty, "Reason.", At(2)));

        AssertUntouchedUnderReview(request);
    }

    [Fact]
    public void Decision_RejectsAnEmptyReviewerWithoutMutating()
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => request.Reject(Guid.NewGuid(), Guid.Empty, "Reason.", At(2)));

        AssertUntouchedUnderReview(request);
    }

    [Theory]
    [InlineData("RequestChanges")]
    [InlineData("Reject")]
    [InlineData("Approve")]
    public void Decision_BeforeTheSubmissionInstant_IsRejectedWithoutMutating(string decision)
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => Pick(decision)(request, Guid.NewGuid(), "Reason.", CreatedAt));

        AssertUntouchedUnderReview(request);
    }

    [Fact]
    public void Decision_BeforeTheRequestsLastChange_IsRejectedWithoutMutating()
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => request.Reject(Guid.NewGuid(), Reviewer, "Reason.", At(1).AddTicks(-1)));

        AssertUntouchedUnderReview(request);
    }

    [Fact]
    public void Decision_RejectsAReusedReviewIdAcrossCycles()
    {
        var request = CreateDraft();
        var reviewId = Guid.NewGuid();
        request.Submit(Guid.NewGuid(), Creator, At(1));
        request.RequestChanges(reviewId, Reviewer, "Fix.", At(2));
        request.Resubmit(Guid.NewGuid(), Creator, At(3));

        Assert.Throws<ArgumentException>(() => request.Reject(reviewId, Reviewer, "No.", At(4)));

        Assert.Equal(PropertyRegistrationRequestStatus.UnderReview, request.Status);
        Assert.Single(request.Reviews);
    }

    [Fact]
    public void Decision_AfterResubmission_IsBoundToTheLatestVersion()
    {
        var request = CreateChangesRequired();
        request.Resubmit(Guid.NewGuid(), Creator, At(3));

        request.Approve(Guid.NewGuid(), Reviewer, Guid.NewGuid(), "Now fine.", At(4));

        Assert.Equal(2, request.Reviews.Count);
        Assert.Equal(1, request.Reviews[0].SubmissionVersion);
        Assert.Equal(request.Submissions[0].Id, request.Reviews[0].SubmissionId);
        Assert.Equal(2, request.Reviews[1].SubmissionVersion);
        Assert.Equal(request.Submissions[1].Id, request.Reviews[1].SubmissionId);
    }

    [Theory]
    [MemberData(nameof(NotUnderReview))]
    public void Decision_FromAnyStateOtherThanUnderReview_IsRejectedWithoutMutating(Func<PropertyRegistrationRequest> create)
    {
        foreach (var decision in new[] { RequestChanges, Reject, Approve })
        {
            var request = create();
            var status = request.Status;
            var reviews = request.Reviews.Count;
            var updatedAt = request.UpdatedAt;

            Assert.Throws<InvalidOperationException>(() => decision(request, Guid.NewGuid(), "Reason.", At(10)));

            Assert.Equal(status, request.Status);
            Assert.Equal(reviews, request.Reviews.Count);
            Assert.Equal(updatedAt, request.UpdatedAt);
        }
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> NotUnderReview() =>
        new()
        {
            () => CreateDraft(),
            CreateChangesRequired,
            CreateApproved,
            CreateRejected,
            CreateWithdrawn
        };

    [Theory]
    [MemberData(nameof(WithdrawableRequests))]
    public void Withdraw_RecordsActorReasonAndTimestamp(Func<PropertyRegistrationRequest> create)
    {
        var request = create();

        request.Withdraw(Editor, "  Changed my mind.  ", At(5));

        Assert.Equal(PropertyRegistrationRequestStatus.Withdrawn, request.Status);
        Assert.NotNull(request.Withdrawal);
        Assert.Equal(Editor, request.Withdrawal.WithdrawnByUserId);
        Assert.Equal("Changed my mind.", request.Withdrawal.Reason);
        Assert.Equal(At(5), request.Withdrawal.WithdrawnAt);
        Assert.Equal(Editor, request.LastModifiedByUserId);
        Assert.Null(request.ResolvedPropertyId);
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> WithdrawableRequests() =>
        new()
        {
            CreateUnderReview,
            CreateChangesRequired
        };

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Withdraw_RejectsABlankReasonWithoutMutating(string reason)
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => request.Withdraw(Creator, reason, At(5)));

        AssertUntouchedUnderReview(request);
        Assert.Null(request.Withdrawal);
    }

    [Fact]
    public void Withdraw_RejectsAnEmptyActorAndAnEarlierTimestampWithoutMutating()
    {
        var request = CreateUnderReview();

        Assert.Throws<ArgumentException>(() => request.Withdraw(Guid.Empty, "Reason.", At(5)));
        Assert.Throws<ArgumentException>(() => request.Withdraw(Creator, "Reason.", CreatedAt));

        AssertUntouchedUnderReview(request);
        Assert.Null(request.Withdrawal);
    }

    [Theory]
    [MemberData(nameof(NotWithdrawable))]
    public void Withdraw_FromAnyOtherState_IsRejectedWithoutMutating(Func<PropertyRegistrationRequest> create)
    {
        var request = create();
        var status = request.Status;
        var withdrawal = request.Withdrawal;

        Assert.Throws<InvalidOperationException>(() => request.Withdraw(Creator, "Reason.", At(10)));

        Assert.Equal(status, request.Status);
        Assert.Same(withdrawal, request.Withdrawal);
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> NotWithdrawable() =>
        new()
        {
            () => CreateDraft(),
            CreateApproved,
            CreateRejected,
            CreateWithdrawn
        };

    [Fact]
    public void ApprovedRequest_CannotBeWithdrawn_AndWithdrawnRequest_CannotBeApproved()
    {
        var approved = CreateApproved();
        var withdrawn = CreateWithdrawn();

        Assert.Throws<InvalidOperationException>(() => approved.Withdraw(Creator, "Reason.", At(10)));
        Assert.Throws<InvalidOperationException>(
            () => withdrawn.Approve(Guid.NewGuid(), Reviewer, Guid.NewGuid(), "Reason.", At(10)));

        Assert.NotNull(approved.ResolvedPropertyId);
        Assert.Null(approved.Withdrawal);
        Assert.Null(withdrawn.ResolvedPropertyId);
        Assert.NotNull(withdrawn.Withdrawal);
    }

    [Theory]
    [MemberData(nameof(TerminalRequests))]
    public void TerminalRequests_RejectEveryOperationWithoutMutating(Func<PropertyRegistrationRequest> create)
    {
        var request = create();
        var status = request.Status;
        var updatedAt = request.UpdatedAt;
        var submissions = request.Submissions.Count;
        var reviews = request.Reviews.Count;

        Assert.Throws<InvalidOperationException>(() => request.UpdateDetails(CompleteUnit(bedrooms: 8), Editor, At(10)));
        Assert.Throws<InvalidOperationException>(() => request.Submit(Guid.NewGuid(), Creator, At(10)));
        Assert.Throws<InvalidOperationException>(() => request.Resubmit(Guid.NewGuid(), Creator, At(10)));
        Assert.Throws<InvalidOperationException>(() => request.RequestChanges(Guid.NewGuid(), Reviewer, "R.", At(10)));
        Assert.Throws<InvalidOperationException>(() => request.Reject(Guid.NewGuid(), Reviewer, "R.", At(10)));
        Assert.Throws<InvalidOperationException>(() => request.Approve(Guid.NewGuid(), Reviewer, Guid.NewGuid(), "R.", At(10)));
        Assert.Throws<InvalidOperationException>(() => request.Withdraw(Creator, "R.", At(10)));

        Assert.Equal(status, request.Status);
        Assert.Equal(updatedAt, request.UpdatedAt);
        Assert.Equal(submissions, request.Submissions.Count);
        Assert.Equal(reviews, request.Reviews.Count);
    }

    public static TheoryData<Func<PropertyRegistrationRequest>> TerminalRequests() =>
        new()
        {
            CreateApproved,
            CreateRejected,
            CreateWithdrawn
        };

    private static void AssertUntouchedUnderReview(PropertyRegistrationRequest request)
    {
        Assert.Equal(PropertyRegistrationRequestStatus.UnderReview, request.Status);
        Assert.Empty(request.Reviews);
        Assert.Null(request.ResolvedPropertyId);
        Assert.Equal(At(1), request.UpdatedAt);
        Assert.Equal(Creator, request.LastModifiedByUserId);
    }
}
