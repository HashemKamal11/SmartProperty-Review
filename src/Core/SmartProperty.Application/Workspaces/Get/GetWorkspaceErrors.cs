using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Get;

internal static class GetWorkspaceErrors
{
    public static readonly Error Forbidden = new(
        "authorization.forbidden",
        "You do not have permission to perform this action.",
        ErrorType.Forbidden);
}
