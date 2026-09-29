#nullable enable
namespace SmartProperty.Api.Contracts.Administration;

/// <summary>
/// One entry of the workspace access request review queue.
/// </summary>
/// <remarks>
/// Carries what a reviewer needs to recognize the applicant and decide: who asked, for which workspace, when, and
/// what has been decided so far. No credential, token, role, or permission data appears here.
/// </remarks>
public sealed record WorkspaceAccessRequestResponse(
    Guid RequestId,
    Guid UserId,
    string UserEmail,
    string UserFirstName,
    string UserLastName,
    Guid WorkspaceId,
    string WorkspaceName,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId);
