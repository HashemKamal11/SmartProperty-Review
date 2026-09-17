using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Register;

internal static class RegisterErrors
{
    public static readonly Error EmailAlreadyExists = new(
        "users.email_already_exists",
        "A user with this email already exists.",
        ErrorType.Conflict);

    public static readonly Error WorkspaceNotFound = new(
        "workspaces.not_found",
        "The selected workspace was not found.",
        ErrorType.NotFound);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
