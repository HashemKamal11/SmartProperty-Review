namespace SmartProperty.Domain.Workspaces;

public sealed class WorkspaceMembership
{
    private WorkspaceMembership()
    {
    }

    public WorkspaceMembership(Guid id, Guid userId, Guid workspaceId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace membership id must not be empty.", nameof(id));
        }

        ValidateRequiredId(userId, nameof(userId));
        ValidateRequiredId(workspaceId, nameof(workspaceId));

        Id = id;
        UserId = userId;
        WorkspaceId = workspaceId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
