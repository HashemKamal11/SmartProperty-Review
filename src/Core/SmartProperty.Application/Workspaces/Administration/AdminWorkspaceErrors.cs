using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Administration;

internal static class AdminWorkspaceErrors
{
    public static readonly Error NotFound = new("workspace_admin.not_found", "The workspace was not found.", ErrorType.NotFound);
    public static Error ValidationFailed(string message) => new("workspace_admin.validation_failed", message, ErrorType.Validation);
}