using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class OneTimeCodeRepository(ApplicationDbContext dbContext) : IOneTimeCodeRepository
{
    public Task<OneTimeCode?> GetLatestAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        return dbContext.OneTimeCodes
            .Where(code => code.UserId == userId && code.Purpose == purpose)
            .OrderByDescending(code => code.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OneTimeCode>> GetUnconsumedAsync(
        Guid userId,
        OneTimeCodePurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        return await dbContext.OneTimeCodes
            .Where(code => code.UserId == userId && code.Purpose == purpose && code.ConsumedAt == null)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(OneTimeCode code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        await dbContext.OneTimeCodes.AddAsync(code, cancellationToken);
    }
}
