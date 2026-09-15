namespace SmartProperty.Domain.Workspaces;

public sealed class WorkspaceAccessRequest
{
    private WorkspaceAccessRequest()
    {
    }

    public WorkspaceAccessRequest(
        Guid id,
        Guid userId,
        Guid workspaceId,
        DateTimeOffset requestedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace access request id must not be empty.", nameof(id));
        }

        ValidateRequiredId(userId, nameof(userId));
        ValidateRequiredId(workspaceId, nameof(workspaceId));

        Id = id;
        UserId = userId;
        WorkspaceId = workspaceId;
        Status = WorkspaceAccessRequestStatus.Pending;
        RequestedAt = requestedAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public WorkspaceAccessRequestStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }

    public void Approve(Guid reviewedByUserId, DateTimeOffset reviewedAt)
    {
        ValidateReview(reviewedByUserId, reviewedAt);

        Status = WorkspaceAccessRequestStatus.Approved;
        ReviewedAt = reviewedAt;
        ReviewedByUserId = reviewedByUserId;
    }

    public void Reject(Guid reviewedByUserId, DateTimeOffset reviewedAt)
    {
        ValidateReview(reviewedByUserId, reviewedAt);

        Status = WorkspaceAccessRequestStatus.Rejected;
        ReviewedAt = reviewedAt;
        ReviewedByUserId = reviewedByUserId;
    }

    private void ValidateReview(Guid reviewedByUserId, DateTimeOffset reviewedAt)
    {
        if (Status != WorkspaceAccessRequestStatus.Pending)
        {
            throw new InvalidOperationException("Only pending workspace access requests can be reviewed.");
        }

        ValidateRequiredId(reviewedByUserId, nameof(reviewedByUserId));

        if (reviewedAt < RequestedAt)
        {
            throw new ArgumentException(
                "Reviewed date cannot be earlier than the requested date.",
                nameof(reviewedAt));
        }
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
