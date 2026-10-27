using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// The account check every write starts with: the persisted user decides, with the established errors.
/// </summary>
public sealed class ActiveAccountGateTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();

    [Fact]
    public async Task AnActiveAccountPassesWithNoError()
    {
        Seed(UserStatus.Active);

        Assert.Null(await ActiveAccountGate.CheckAsync(_users, UserId));
    }

    [Fact]
    public async Task AMissingAccountIsTheSame401AsNoToken()
    {
        var error = await ActiveAccountGate.CheckAsync(_users, UserId);

        Assert.Same(ListingSubmissionErrors.Unauthorized, error);
        Assert.Equal("authentication.unauthorized", error!.Code);
        Assert.Equal(ErrorType.Unauthorized, error.Type);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task EveryNonActiveStatusIsTheOneAccountUnavailableError(UserStatus status)
    {
        Seed(status);

        var error = await ActiveAccountGate.CheckAsync(_users, UserId);

        Assert.Same(MeErrors.AccountUnavailable, error);
        Assert.Equal("authentication.account_unavailable", error!.Code);
        Assert.Equal(ErrorType.Forbidden, error.Type);
        Assert.DoesNotContain(status.ToString(), error.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheStatusIsReadFromTheRecordEachTime()
    {
        Seed(UserStatus.Active);
        Assert.Null(await ActiveAccountGate.CheckAsync(_users, UserId));

        Seed(UserStatus.Suspended);
        Assert.Same(MeErrors.AccountUnavailable, await ActiveAccountGate.CheckAsync(_users, UserId));

        Seed(UserStatus.Active);
        Assert.Null(await ActiveAccountGate.CheckAsync(_users, UserId));
        Assert.Equal(3, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task ALookupFailurePropagatesAndIsNeverAnAnswer()
    {
        var failing = new FailingUserRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ActiveAccountGate.CheckAsync(failing, UserId));
    }

    [Fact]
    public async Task TheCallersTokenReachesTheLookup()
    {
        using var source = new CancellationTokenSource();
        var recording = new FailingUserRepository { Throw = false };

        await ActiveAccountGate.CheckAsync(recording, UserId, source.Token);

        Assert.Equal(source.Token, recording.CancellationToken);
        Assert.Equal(UserId, recording.LastRequestedId);
    }

    [Fact]
    public async Task ARepositoryIsRequired()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ActiveAccountGate.CheckAsync(null!, UserId));
    }

    private void Seed(UserStatus status)
    {
        var user = new User(UserId, "someone@example.test", "Some", "One", Created);

        switch (status)
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

        _users.Seed(user);
    }

    private sealed class FailingUserRepository : IUserRepository
    {
        public bool Throw { get; set; } = true;

        public Guid? LastRequestedId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            LastRequestedId = id;
            CancellationToken = cancellationToken;

            return Throw
                ? throw new InvalidOperationException("identity unavailable")
                : Task.FromResult<User?>(null);
        }

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
