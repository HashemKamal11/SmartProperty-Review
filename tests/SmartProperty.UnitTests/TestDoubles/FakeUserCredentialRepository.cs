using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakeUserCredentialRepository : IUserCredentialRepository
{
    private readonly Dictionary<Guid, UserCredential> _credentialsByUserId = [];

    public void Seed(UserCredential credential)
    {
        _credentialsByUserId[credential.UserId] = credential;
    }

    public Task<UserCredential?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_credentialsByUserId.GetValueOrDefault(userId));
    }

    public Task AddAsync(UserCredential credential, CancellationToken cancellationToken = default)
    {
        Seed(credential);

        return Task.CompletedTask;
    }
}
