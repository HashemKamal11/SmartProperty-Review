using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class PhoneVerificationRepository(ApplicationDbContext dbContext) : IPhoneVerificationRepository
{
    public Task<PhoneVerification?> GetLatestByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        return dbContext.PhoneVerifications
            .Where(verification => verification.UserId == userId)
            .OrderByDescending(verification => verification.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PhoneVerification>> GetUnconsumedByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        return await dbContext.PhoneVerifications
            .Where(verification => verification.UserId == userId && verification.ConsumedAt == null)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(PhoneVerification verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification);

        await dbContext.PhoneVerifications.AddAsync(verification, cancellationToken);
    }
}
