namespace SmartProperty.Application.Workspaces.Administration;

public sealed record AdminWorkspaceDetails(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);