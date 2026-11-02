#nullable enable
namespace SmartProperty.Api.Contracts.Workspaces;

public sealed record AdminWorkspaceResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);