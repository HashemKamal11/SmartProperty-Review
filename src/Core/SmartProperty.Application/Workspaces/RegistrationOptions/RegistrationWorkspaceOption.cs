namespace SmartProperty.Application.Workspaces.RegistrationOptions;

/// <summary>
/// One workspace a prospective user may choose when registering: its id, which registration takes, and the name a
/// person recognizes it by.
/// </summary>
/// <remarks>
/// A read model rather than an entity, projected in the database. It is shown before authentication, so it carries
/// nothing else — no timestamps, memberships, roles, or permissions.
/// </remarks>
public sealed record RegistrationWorkspaceOption(
    Guid Id,
    string Name);
