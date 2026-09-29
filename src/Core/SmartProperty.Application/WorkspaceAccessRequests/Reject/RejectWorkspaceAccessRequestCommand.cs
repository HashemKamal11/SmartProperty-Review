using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.WorkspaceAccessRequests.Reject;

/// <summary>
/// Rejects one pending workspace access request.
/// </summary>
/// <remarks>
/// As with approval, the reviewer is not an input: it comes from the authenticated request through
/// <see cref="Abstractions.Identity.ICurrentUser"/>.
/// </remarks>
public sealed record RejectWorkspaceAccessRequestCommand(Guid WorkspaceAccessRequestId)
    : ICommand<RejectWorkspaceAccessRequestResult>;
