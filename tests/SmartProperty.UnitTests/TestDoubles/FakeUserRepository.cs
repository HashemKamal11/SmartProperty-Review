using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory user lookup. Normalizes the email the same way <see cref="User"/> does, so a handler that passes
/// the raw command value through still finds a seeded user.
/// </summary>
internal sealed class FakeUserRepository : IUserRepository
{
    private readonly Dictionary<Guid, User> _usersById = [];

    public int GetByEmailCallCount { get; private set; }

    public int GetByIdCallCount { get; private set; }

    public void Seed(User user)
    {
        _usersById[user.Id] = user;
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;

        return Task.FromResult(_usersById.GetValueOrDefault(id));
    }

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        GetByEmailCallCount++;

        var normalizedEmail = email.Trim().ToLowerInvariant();

        return Task.FromResult(
            _usersById.Values.FirstOrDefault(user => user.Email == normalizedEmail));
    }

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        Seed(user);

        return Task.CompletedTask;
    }
}
