using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Workspaces.Administration.Get;

public sealed record GetAdminWorkspaceQuery(Guid WorkspaceId) : IQuery<AdminWorkspaceDetails>;