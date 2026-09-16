using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class UserCredentialRepository(ApplicationDbContext dbContext) : IUserCredentialRepository
{
    public Task<UserCredential?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        return dbContext.UserCredentials.FirstOrDefaultAsync(
            credential => credential.UserId == userId,
            cancellationToken);
    }

    public async Task AddAsync(UserCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        await dbContext.UserCredentials.AddAsync(credential, cancellationToken);
    }
}
