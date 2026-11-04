namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// One immutable reviewer decision, bound to the exact submitted version it judged. Every decision, approval
/// included, records the reviewer, a reason, and the instant; an approval also records the property it resolved to.
/// </summary>
public sealed class PropertyRegistrationRequestReview
{
    private PropertyRegistrationRequestReview()
    {
        Reason = string.Empty;
    }

    internal PropertyRegistrationRequestReview(
        Guid id,
        PropertyRegistrationDecision decision,
        Guid reviewerUserId,
        string reason,
        DateTimeOffset decidedAt,
        PropertyRegistrationRequestSubmission submission,
        Guid? resolvedPropertyId)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "Decision is not supported.");
        }

        Id = PropertyRegistrationGuard.RequireId(id, nameof(id));
        ReviewerUserId = PropertyRegistrationGuard.RequireId(reviewerUserId, nameof(reviewerUserId));
        Reason = PropertyRegistrationGuard.RequireText(reason, nameof(reason));

        if (decidedAt < submission.SubmittedAt)
        {
            throw new ArgumentException(
                "Decision date cannot be earlier than the submission date.",
                nameof(decidedAt));
        }

        if (decision == PropertyRegistrationDecision.Approved)
        {
            ResolvedPropertyId = PropertyRegistrationGuard.RequireId(
                resolvedPropertyId ?? Guid.Empty,
                nameof(resolvedPropertyId));
        }
        else if (resolvedPropertyId is not null)
        {
            throw new ArgumentException(
                "Only an approval resolves to a property.",
                nameof(resolvedPropertyId));
        }

        Decision = decision;
        DecidedAt = decidedAt;
        SubmissionId = submission.Id;
        SubmissionVersion = submission.VersionNumber;
    }

    public Guid Id { get; private set; }
    public PropertyRegistrationDecision Decision { get; private set; }
    public Guid ReviewerUserId { get; private set; }
    public string Reason { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }
    public Guid SubmissionId { get; private set; }
    public int SubmissionVersion { get; private set; }
    public Guid? ResolvedPropertyId { get; private set; }
}
