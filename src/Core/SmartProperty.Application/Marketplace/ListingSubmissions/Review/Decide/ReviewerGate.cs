using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;

/// <summary>The reviewer: the authenticated, Active user who holds the Platform decide permission.</summary>
internal readonly record struct ReviewerGate(Guid UserId, Error? Error)
{
    public static async Task<ReviewerGate> ResolveAsync(
        ICurrentUser currentUser,
        IUserRepository users,
        IReviewDecisionAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return new ReviewerGate(Guid.Empty, accountError);
        }

        // Only an explicit true allows; an exception from the authorizer is never turned into access.
        return await authorizer.IsAuthorizedAsync(acting.UserId, cancellationToken)
            ? new ReviewerGate(acting.UserId, null)
            : new ReviewerGate(Guid.Empty, ReviewErrors.Forbidden);
    }
}
