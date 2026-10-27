using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test-only stand-in for the user repository, so an account's persisted status can be controlled without a
/// database.
/// </summary>
/// <remarks>
/// Any user id is an Active account unless a test says otherwise, which keeps tests that are not about the account
/// free of setup. A test sets a status for one id, or <see langword="null"/> for an account that does not exist. The
/// status is read on every call, exactly like the real repository, so a test can change it between two requests and
/// prove nothing is remembered. It exists only in test infrastructure.
/// </remarks>
internal sealed class StubUserRepository : IUserRepository
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<Guid, UserStatus?> _statuses = [];

    public int GetByIdCallCount { get; private set; }

    public Guid? LastRequestedId { get; private set; }

    /// <summary>Sets one account's status; null means the account does not exist.</summary>
    public void Set(Guid id, UserStatus? status)
    {
        _statuses[id] = status;
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;

        var status = _statuses.TryGetValue(id, out var configured) ? configured : UserStatus.Active;

        if (status is not { } value)
        {
            return Task.FromResult<User?>(null);
        }

        var user = new User(id, $"{id}@example.test", "Test", "User", Created);

        switch (value)
        {
            case UserStatus.Active:
                user.Activate(Created);
                break;
            case UserStatus.Suspended:
                user.Suspend(Created);
                break;
            case UserStatus.Deactivated:
                user.Deactivate(Created);
                break;
            case UserStatus.Pending:
            default:
                break;
        }

        return Task.FromResult<User?>(user);
    }

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Only lookup by id is needed here.");
    }

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Nothing here creates users.");
    }
}
