using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory phone verifications. "Latest" is decided by creation time, like the real repository.
/// </summary>
internal sealed class FakePhoneVerificationRepository : IPhoneVerificationRepository
{
    private readonly List<PhoneVerification> _verifications = [];

    public IReadOnlyList<PhoneVerification> All => _verifications;

    public void Seed(PhoneVerification verification)
    {
        _verifications.Add(verification);
    }

    public Task<PhoneVerification?> GetLatestByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_verifications
            .Where(verification => verification.UserId == userId)
            .OrderByDescending(verification => verification.CreatedAt)
            .FirstOrDefault());
    }

    public Task<IReadOnlyList<PhoneVerification>> GetUnconsumedByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PhoneVerification> unconsumed = _verifications
            .Where(verification => verification.UserId == userId && verification.ConsumedAt is null)
            .ToList();

        return Task.FromResult(unconsumed);
    }

    public Task AddAsync(PhoneVerification verification, CancellationToken cancellationToken = default)
    {
        _verifications.Add(verification);

        return Task.CompletedTask;
    }
}
