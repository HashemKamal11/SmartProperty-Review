using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Administration.Create;

internal static class CreateWorkspaceCommandValidator
{
    public static Error? Validate(CreateWorkspaceCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return AdminWorkspaceErrors.ValidationFailed("Workspace name is required.");
        // Check the original input: trimming must not silently accept control characters.
        if (command.Name.Any(char.IsControl))
            return AdminWorkspaceErrors.ValidationFailed("Workspace name must not contain control characters.");
        if (command.Name.Trim().Length > 200)
            return AdminWorkspaceErrors.ValidationFailed("Workspace name must contain 1–200 UTF-16 code units after trimming.");
        return null;
    }
}