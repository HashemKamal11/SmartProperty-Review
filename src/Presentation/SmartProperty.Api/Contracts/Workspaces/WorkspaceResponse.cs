#nullable enable
namespace SmartProperty.Api.Contracts.Workspaces;

/// <summary>A workspace's id and name. Nothing more is exposed.</summary>
public sealed record WorkspaceResponse(Guid Id, string Name);
