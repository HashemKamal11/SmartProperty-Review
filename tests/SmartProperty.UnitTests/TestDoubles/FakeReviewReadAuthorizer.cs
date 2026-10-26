using SmartProperty.Application.Abstractions.Authorization;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// A test-only <see cref="IReviewReadAuthorizer"/> that answers exactly what a test configured. Production has no
/// implementation at all.
/// </summary>
internal sealed class FakeReviewReadAuthorizer(bool authorized) : IReviewReadAuthorizer
{
    /// <summary>When set, the authorizer throws it instead of answering, as a failed lookup would.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public Guid? LastUserId { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task<bool> IsAuthorizedAsync(Guid reviewerUserId, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastUserId = reviewerUserId;
        CancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(authorized);
    }
}
