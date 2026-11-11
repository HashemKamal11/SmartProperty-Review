namespace SmartProperty.Application.Abstractions.Authorization;

/// <summary>
/// Decides whether a user may approve or reject a marketplace listing submission.
/// </summary>
/// <remarks>
/// Separate from <see cref="IReviewReadAuthorizer"/> on purpose: the authority to read the queue and the authority to
/// decide are independent. Only an explicit <see langword="true"/> allows; a failure to determine the answer is an
/// exception, never <see langword="true"/>. Implementations must check the approved Platform-scoped decide permission
/// and nothing else.
/// </remarks>
public interface IReviewDecisionAuthorizer
{
    Task<bool> IsAuthorizedAsync(Guid reviewerUserId, CancellationToken cancellationToken = default);
}
