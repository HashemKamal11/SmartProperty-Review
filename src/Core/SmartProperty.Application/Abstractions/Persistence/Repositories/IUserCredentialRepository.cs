using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IUserCredentialRepository
{
    Task<UserCredential?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AddAsync(UserCredential credential, CancellationToken cancellationToken = default);
}
