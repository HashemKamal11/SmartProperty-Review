using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Workspaces.RegistrationOptions;

/// <summary>
/// Reads the workspaces a prospective user may choose from when registering. Takes no input.
/// </summary>
public sealed record GetRegistrationWorkspaceOptionsQuery : IQuery<IReadOnlyList<RegistrationWorkspaceOption>>;
