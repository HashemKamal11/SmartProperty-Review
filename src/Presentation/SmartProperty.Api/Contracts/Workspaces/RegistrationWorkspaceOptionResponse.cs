#nullable enable
namespace SmartProperty.Api.Contracts.Workspaces;

/// <summary>
/// One workspace the registration form can offer. Served anonymously, so it carries only the id registration takes
/// and the name a person chooses by.
/// </summary>
public sealed record RegistrationWorkspaceOptionResponse(
    Guid Id,
    string Name);
