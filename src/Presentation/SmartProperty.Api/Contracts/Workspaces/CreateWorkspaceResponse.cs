#nullable enable
namespace SmartProperty.Api.Contracts.Workspaces;

/// <param name="AdminUserId">The id of the account created as the workspace admin, or null when none was requested.</param>
public sealed record CreateWorkspaceResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? AdminUserId);
