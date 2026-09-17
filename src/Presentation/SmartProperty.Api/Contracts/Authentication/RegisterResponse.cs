#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record RegisterResponse(
    Guid UserId,
    string Status,
    Guid WorkspaceId,
    Guid WorkspaceAccessRequestId,
    string WorkspaceAccessStatus);
