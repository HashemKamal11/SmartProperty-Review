namespace SmartProperty.Application.Abstractions.Authorization;

/// <summary>
/// Decides whether a user may read the Platform Admin marketplace review queue and review details.
/// </summary>
/// <remarks>
/// This is a dedicated seam, not a general permission lookup. It exists so the review read handlers can fail closed
/// until an approved Platform permission, and its provisioning, are available. Reading a submission for review is
/// separate from the authority to approve or reject it, which the Domain verifies on its own.
///
/// Only an explicit <see langword="true"/> allows access. There is deliberately no implementation and no default in
/// this assembly, and none may be registered in production that returns <see langword="true"/> unconditionally,
/// reuses an unrelated permission, treats Workspace permissions or membership as Platform authority, or looks at a
/// role name. The eventual implementation must check the approved Platform permission against the Platform scope.
/// A failure to determine the answer must be an exception, never <see langword="true"/>.
/// </remarks>
public interface IReviewReadAuthorizer
{
    Task<bool> IsAuthorizedAsync(Guid reviewerUserId, CancellationToken cancellationToken = default);
}
