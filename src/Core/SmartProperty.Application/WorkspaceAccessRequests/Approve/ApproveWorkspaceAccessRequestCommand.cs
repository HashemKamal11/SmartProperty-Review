using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.WorkspaceAccessRequests.Approve;

/// <summary>
/// Approves one pending workspace access request.
/// </summary>
/// <remarks>
/// The request identifier is the only input. The reviewer is deliberately absent: it is resolved from the
/// authenticated request through <see cref="Abstractions.Identity.ICurrentUser"/>, so a caller cannot record
/// the decision against somebody else.
/// </remarks>
public sealed record ApproveWorkspaceAccessRequestCommand(Guid WorkspaceAccessRequestId)
    : ICommand<ApproveWorkspaceAccessRequestResult>;
