using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Domain.OwnerVerification;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>An in-memory owner profile store.</summary>
internal sealed class FakeOwnerProfileRepository : IOwnerProfileRepository
{
    public List<OwnerProfile> Stored { get; } = [];

    public List<OwnerProfile> Added { get; } = [];

    public void Seed(OwnerProfile profile) => Stored.Add(profile);

    public Task<OwnerProfile?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Stored.FirstOrDefault(profile => profile.UserId == userId));
    }

    public Task<OwnerProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Stored.FirstOrDefault(profile => profile.Id == id));
    }

    public Task AddAsync(OwnerProfile profile, CancellationToken cancellationToken = default)
    {
        Added.Add(profile);
        Stored.Add(profile);

        return Task.CompletedTask;
    }

    public Task<bool> NationalIdUsedByAnotherAsync(
        string nationalId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Stored.Any(profile => profile.NationalId == nationalId && profile.UserId != userId));
    }

    public Task<OwnerReviewPage> ListForReviewAsync(
        OwnerVerificationStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var rows = Stored
            .Where(profile => profile.Status != OwnerVerificationStatus.Draft)
            .Where(profile => status is null || profile.Status == status)
            .ToList();

        var items = rows
            .Skip(skip)
            .Take(take)
            .Select(profile => new OwnerReviewItem(
                profile.Id,
                profile.UserId,
                "First",
                "Last",
                "owner@example.com",
                null,
                profile.Status,
                profile.SubmittedAt,
                profile.ReviewedAt,
                profile.ReviewedByUserId))
            .ToList();

        return Task.FromResult(new OwnerReviewPage(items, rows.Count));
    }
}
