using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Workspaces.Get;

/// <summary>Reads one workspace by id.</summary>
public sealed record GetWorkspaceQuery(Guid WorkspaceId) : IQuery<WorkspaceDetails>;
