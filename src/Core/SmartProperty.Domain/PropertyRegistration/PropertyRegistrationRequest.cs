namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// A Workspace's request to have a physical property registered in the platform registry. The request owns its
/// working details, its immutable submitted versions, and the immutable reviewer decisions made about them. It
/// never creates a <c>Property</c>: an approval only records the id of the property the request resolved to.
/// </summary>
/// <remarks>
/// The domain performs no role or Workspace permission checks; it records the acting user ids it is given and the
/// application layer decides who may act. Every operation validates fully before it changes anything, so a
/// rejected call leaves the request exactly as it was.
/// </remarks>
public sealed class PropertyRegistrationRequest
{
    private readonly List<PropertyRegistrationRequestSubmission> _submissions = [];
    private readonly List<PropertyRegistrationRequestReview> _reviews = [];

    private PropertyRegistrationRequest()
    {
        BusinessReference = string.Empty;
        Details = null!;
    }

    public PropertyRegistrationRequest(
        Guid id,
        string businessReference,
        Guid workspaceId,
        Guid createdByUserId,
        PropertyRegistrationRequestDetails details,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(details);

        Id = PropertyRegistrationGuard.RequireId(id, nameof(id));
        BusinessReference = PropertyRegistrationGuard.RequireText(businessReference, nameof(businessReference));
        WorkspaceId = PropertyRegistrationGuard.RequireId(workspaceId, nameof(workspaceId));
        CreatedByUserId = PropertyRegistrationGuard.RequireId(createdByUserId, nameof(createdByUserId));
        Details = details;
        Status = PropertyRegistrationRequestStatus.Draft;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        LastModifiedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }
    public string BusinessReference { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public PropertyRegistrationRequestStatus Status { get; private set; }

    /// <summary>The working copy. Submitted content lives, frozen, in <see cref="Submissions"/>.</summary>
    public PropertyRegistrationRequestDetails Details { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The instant of the latest operation that changed this request.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The user behind the latest operation that changed this request.</summary>
    public Guid LastModifiedByUserId { get; private set; }

    /// <summary>The property an approval resolved to; empty until the request is approved.</summary>
    public Guid? ResolvedPropertyId { get; private set; }

    public PropertyRegistrationRequestWithdrawal? Withdrawal { get; private set; }

    public int CurrentVersion => _submissions.Count;

    public IReadOnlyList<PropertyRegistrationRequestSubmission> Submissions => _submissions.AsReadOnly();

    public IReadOnlyList<PropertyRegistrationRequestReview> Reviews => _reviews.AsReadOnly();

    /// <summary>
    /// Replaces the working details. Allowed only while the request is a draft or awaiting changes; the lifecycle
    /// is checked before anything else, a no-op included.
    /// </summary>
    /// <returns>True when the details changed; false when they were already current.</returns>
    public bool UpdateDetails(
        PropertyRegistrationRequestDetails details,
        Guid modifiedByUserId,
        DateTimeOffset modifiedAt)
    {
        if (Status is not (PropertyRegistrationRequestStatus.Draft or PropertyRegistrationRequestStatus.ChangesRequired))
        {
            throw new InvalidOperationException("Only a draft or a request awaiting changes can be edited.");
        }

        ArgumentNullException.ThrowIfNull(details);
        PropertyRegistrationGuard.RequireId(modifiedByUserId, nameof(modifiedByUserId));

        // A no-op is decided before the timestamp is examined, as in Property.UpdateDetails.
        if (Details == details)
        {
            return false;
        }

        EnsureNotEarlierThanLastChange(modifiedAt, nameof(modifiedAt));

        Details = details;
        Touch(modifiedByUserId, modifiedAt);

        return true;
    }

    /// <summary>Submits a draft for review as version 1.</summary>
    public void Submit(Guid submissionId, Guid submittedByUserId, DateTimeOffset submittedAt)
    {
        EnsureStatus(PropertyRegistrationRequestStatus.Draft, "Only a draft can be submitted.");
        AppendSubmission(submissionId, submittedByUserId, submittedAt);
    }

    /// <summary>Resubmits corrected details as the next version; identity and reference are unchanged.</summary>
    public void Resubmit(Guid submissionId, Guid submittedByUserId, DateTimeOffset submittedAt)
    {
        EnsureStatus(PropertyRegistrationRequestStatus.ChangesRequired, "Only a request awaiting changes can be resubmitted.");
        AppendSubmission(submissionId, submittedByUserId, submittedAt);
    }

    public void RequestChanges(Guid reviewId, Guid reviewerUserId, string reason, DateTimeOffset decidedAt)
    {
        Decide(
            reviewId,
            PropertyRegistrationDecision.ChangesRequested,
            reviewerUserId,
            reason,
            decidedAt,
            resolvedPropertyId: null,
            PropertyRegistrationRequestStatus.ChangesRequired);
    }

    public void Reject(Guid reviewId, Guid reviewerUserId, string reason, DateTimeOffset decidedAt)
    {
        Decide(
            reviewId,
            PropertyRegistrationDecision.Rejected,
            reviewerUserId,
            reason,
            decidedAt,
            resolvedPropertyId: null,
            PropertyRegistrationRequestStatus.Rejected);
    }

    public void Approve(
        Guid reviewId,
        Guid reviewerUserId,
        Guid resolvedPropertyId,
        string reason,
        DateTimeOffset decidedAt)
    {
        Decide(
            reviewId,
            PropertyRegistrationDecision.Approved,
            reviewerUserId,
            reason,
            decidedAt,
            resolvedPropertyId,
            PropertyRegistrationRequestStatus.Approved);
    }

    /// <summary>Permanently withdraws a request that is under review or awaiting changes.</summary>
    public void Withdraw(Guid withdrawnByUserId, string reason, DateTimeOffset withdrawnAt)
    {
        if (Status is not (PropertyRegistrationRequestStatus.UnderReview or PropertyRegistrationRequestStatus.ChangesRequired))
        {
            throw new InvalidOperationException("Only a request under review or awaiting changes can be withdrawn.");
        }

        EnsureNotEarlierThanLastChange(withdrawnAt, nameof(withdrawnAt));

        var withdrawal = new PropertyRegistrationRequestWithdrawal(withdrawnByUserId, reason, withdrawnAt);

        Withdrawal = withdrawal;
        Status = PropertyRegistrationRequestStatus.Withdrawn;
        Touch(withdrawnByUserId, withdrawnAt);
    }

    private void AppendSubmission(Guid submissionId, Guid submittedByUserId, DateTimeOffset submittedAt)
    {
        PropertyRegistrationGuard.RequireId(submissionId, nameof(submissionId));
        EnsureNotEarlierThanLastChange(submittedAt, nameof(submittedAt));
        Details.EnsureReadyForSubmission();

        if (_submissions.Exists(submission => submission.Id == submissionId))
        {
            throw new ArgumentException("A submission with this id already exists.", nameof(submissionId));
        }

        var submission = new PropertyRegistrationRequestSubmission(
            submissionId,
            _submissions.Count + 1,
            submittedByUserId,
            submittedAt,
            Details);

        _submissions.Add(submission);
        Status = PropertyRegistrationRequestStatus.UnderReview;
        Touch(submittedByUserId, submittedAt);
    }

    private void Decide(
        Guid reviewId,
        PropertyRegistrationDecision decision,
        Guid reviewerUserId,
        string reason,
        DateTimeOffset decidedAt,
        Guid? resolvedPropertyId,
        PropertyRegistrationRequestStatus resultingStatus)
    {
        EnsureStatus(PropertyRegistrationRequestStatus.UnderReview, "Only a request under review can be decided.");
        EnsureNotEarlierThanLastChange(decidedAt, nameof(decidedAt));

        if (_reviews.Exists(review => review.Id == reviewId))
        {
            throw new ArgumentException("A review with this id already exists.", nameof(reviewId));
        }

        // Constructing the review validates every remaining argument before any state changes.
        var review = new PropertyRegistrationRequestReview(
            reviewId,
            decision,
            reviewerUserId,
            reason,
            decidedAt,
            _submissions[^1],
            resolvedPropertyId);

        _reviews.Add(review);
        ResolvedPropertyId = review.ResolvedPropertyId;
        Status = resultingStatus;
        Touch(reviewerUserId, decidedAt);
    }

    private void EnsureStatus(PropertyRegistrationRequestStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private void EnsureNotEarlierThanLastChange(DateTimeOffset at, string parameterName)
    {
        if (at < UpdatedAt)
        {
            throw new ArgumentException(
                "Date cannot be earlier than the current updated date.",
                parameterName);
        }
    }

    private void Touch(Guid userId, DateTimeOffset at)
    {
        UpdatedAt = at;
        LastModifiedByUserId = userId;
    }
}
