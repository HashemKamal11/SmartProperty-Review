namespace SmartProperty.Application.Workspaces.Administration;

public sealed record AdminWorkspacePage(IReadOnlyList<AdminWorkspaceDetails> Items, long TotalCount);