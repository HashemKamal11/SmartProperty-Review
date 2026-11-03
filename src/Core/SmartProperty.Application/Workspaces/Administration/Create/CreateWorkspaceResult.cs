namespace SmartProperty.Application.Workspaces.Administration.Create;

public sealed record CreateWorkspaceResult(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? AdminUserId);
