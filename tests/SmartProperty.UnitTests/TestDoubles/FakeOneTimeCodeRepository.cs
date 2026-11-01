using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory one-time codes. "Latest" is decided by creation time, like the real repository, and every lookup is
/// scoped to the user and purpose.
/// </summary>
internal sealed class FakeOneTimeCodeRepository : IOneTimeCodeRepository
{
    private readonly List<OneTimeCode> _codes = [];

    public IReadOnlyList<OneTimeCode> All => _codes;

    public void Seed(OneTimeCode code)
    {
        _codes.Add(code);
    }

    public Task<OneTimeCode?> GetLatestAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_codes
            .Where(code => code.UserId == userId && code.Purpose == purpose)
            .OrderByDescending(code => code.CreatedAt)
            .FirstOrDefault());
    }

    public Task<IReadOnlyList<OneTimeCode>> GetUnconsumedAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OneTimeCode> unconsumed = _codes
            .Where(code => code.UserId == userId && code.Purpose == purpose && code.ConsumedAt is null)
            .ToList();

        return Task.FromResult(unconsumed);
    }

    public Task AddAsync(OneTimeCode code, CancellationToken cancellationToken = default)
    {
        _codes.Add(code);

        return Task.CompletedTask;
    }
}
