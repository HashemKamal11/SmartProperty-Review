#nullable enable
namespace SmartProperty.Api.Contracts.Administration;

/// <summary>
/// The state an approval produced: the decision, the resulting account status, and the membership the applicant
/// now holds.
/// </summary>
/// <remarks>
/// <see cref="MembershipId"/> is the membership that exists after the call, whether this approval created it or
/// an earlier one already had. No role id appears, because approval assigns none.
/// </remarks>
public sealed record ApproveWorkspaceAccessRequestResponse(
    Guid RequestId,
    Guid UserId,
    Guid WorkspaceId,
    string RequestStatus,
    string UserStatus,
    Guid MembershipId);
