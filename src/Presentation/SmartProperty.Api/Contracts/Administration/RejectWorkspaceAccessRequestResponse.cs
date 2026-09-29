#nullable enable
namespace SmartProperty.Api.Contracts.Administration;

/// <summary>
/// The state a rejection produced.
/// </summary>
/// <remarks>
/// There is no membership id and no role id, because a rejection creates neither. <see cref="UserStatus"/> is
/// reported so a caller can see the account was left exactly as it was.
/// </remarks>
public sealed record RejectWorkspaceAccessRequestResponse(
    Guid RequestId,
    Guid UserId,
    Guid WorkspaceId,
    string RequestStatus,
    string UserStatus);
