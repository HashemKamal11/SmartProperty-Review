using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Workspaces.Administration.Create;

public sealed record CreateWorkspaceCommand(string? Name) : ICommand<AdminWorkspaceDetails>;