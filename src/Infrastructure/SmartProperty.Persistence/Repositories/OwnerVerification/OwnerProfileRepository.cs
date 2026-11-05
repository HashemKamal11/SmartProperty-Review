using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.OwnerVerification;

internal sealed class OwnerProfileRepository(ApplicationDbContext dbContext) : IOwnerProfileRepository
{
    public Task<OwnerProfile?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return dbContext.OwnerProfiles.FirstOrDefaultAsync(profile => profile.UserId == userId, cancellationToken);
    }

    public Task<OwnerProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.OwnerProfiles.FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken);
    }

    public async Task AddAsync(OwnerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await dbContext.OwnerProfiles.AddAsync(profile, cancellationToken);
    }

    public Task<bool> NationalIdUsedByAnotherAsync(
        string nationalId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.OwnerProfiles
            .AsNoTracking()
            .AnyAsync(profile => profile.NationalId == nationalId && profile.UserId != userId, cancellationToken);
    }

    public async Task<OwnerReviewPage> ListForReviewAsync(
        OwnerVerificationStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least 1.");
        }

        var profiles = dbContext.OwnerProfiles
            .AsNoTracking()
            .Where(profile => profile.Status != OwnerVerificationStatus.Draft);

        if (status is { } wanted)
        {
            profiles = profiles.Where(profile => profile.Status == wanted);
        }

        var total = await profiles.CountAsync(cancellationToken);

        var joined = profiles.Join(
            dbContext.Users,
            profile => profile.UserId,
            user => user.Id,
            (profile, user) => new { Profile = profile, User = user });

        // Waiting profiles are answered in the order they arrived; the decided ones are read newest first. The id
        // breaks ties so paging is stable.
        var ordered = status == OwnerVerificationStatus.UnderReview
            ? joined.OrderBy(row => row.Profile.SubmittedAt).ThenBy(row => row.Profile.Id)
            : joined.OrderByDescending(row => row.Profile.SubmittedAt).ThenBy(row => row.Profile.Id);

        var items = await ordered
            .Skip(skip)
            .Take(take)
            .Select(row => new OwnerReviewItem(
                row.Profile.Id,
                row.User.Id,
                row.User.FirstName,
                row.User.LastName,
                row.User.Email,
                row.User.PhoneNumber,
                row.Profile.Status,
                row.Profile.SubmittedAt,
                row.Profile.ReviewedAt,
                row.Profile.ReviewedByUserId))
            .ToListAsync(cancellationToken);

        return new OwnerReviewPage(items, total);
    }
}
