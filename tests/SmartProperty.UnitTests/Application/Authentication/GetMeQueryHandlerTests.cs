using SmartProperty.Application.Authentication.Me;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real <see cref="GetMeQueryHandler"/>. The identity comes only from <c>ICurrentUser</c>, and the
/// account status is re-read from the repository rather than trusted from the token.
/// </summary>
public sealed class GetMeQueryHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();

    [Fact]
    public async Task NoCurrentUser_ReturnsUnauthorized()
    {
        var handler = new GetMeQueryHandler(new FakeCurrentUser(null), _users);

        var result = await handler.Handle(new GetMeQuery());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.Unauthorized, result.Error);
    }

    [Fact]
    public async Task NoCurrentUser_NeverQueriesTheRepository()
    {
        var handler = new GetMeQueryHandler(new FakeCurrentUser(null), _users);

        await handler.Handle(new GetMeQuery());

        Assert.Equal(0, _users.GetByIdCallCount);
    }

    [Fact]
    public async Task MissingUserRow_ReturnsUnauthorized()
    {
        // A token can outlive the user it names. That is invalid session state, not a missing resource, so it
        // must look identical to an unauthenticated request rather than confirm an unknown id.
        var handler = new GetMeQueryHandler(new FakeCurrentUser(Guid.NewGuid()), _users);

        var result = await handler.Handle(new GetMeQuery());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.Unauthorized, result.Error);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUser_ReturnsAccountUnavailable(UserStatus status)
    {
        var user = SeedUser(status);
        var handler = new GetMeQueryHandler(new FakeCurrentUser(user.Id), _users);

        var result = await handler.Handle(new GetMeQuery());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.AccountUnavailable, result.Error);
    }

    [Fact]
    public async Task ActiveUser_ReturnsTheCurrentProfile()
    {
        var user = SeedUser(UserStatus.Active);
        var handler = new GetMeQueryHandler(new FakeCurrentUser(user.Id), _users);

        var result = await handler.Handle(new GetMeQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value.UserId);
        Assert.Equal("owner@example.com", result.Value.Email);
        Assert.Equal("Owner", result.Value.FirstName);
        Assert.Equal("Example", result.Value.LastName);
    }

    [Fact]
    public void MeResult_ExposesOnlyTheIntendedProfileFields()
    {
        // Guards the shape of the successful response: no credential, token, role, permission, or workspace
        // data may appear here without a deliberate decision.
        var propertyNames = typeof(MeResult)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Email", "FirstName", "LastName", "UserId"], propertyNames);
    }

    [Fact]
    public async Task NullQuery_Throws()
    {
        var handler = new GetMeQueryHandler(new FakeCurrentUser(Guid.NewGuid()), _users);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.Handle(null!));
    }

    private User SeedUser(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), "owner@example.com", "Owner", "Example", CreatedAt);

        switch (status)
        {
            case UserStatus.Active:
                user.Activate(CreatedAt.AddDays(1));
                break;
            case UserStatus.Suspended:
                user.Suspend(CreatedAt.AddDays(1));
                break;
            case UserStatus.Deactivated:
                user.Deactivate(CreatedAt.AddDays(1));
                break;
            case UserStatus.Pending:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled user status.");
        }

        _users.Seed(user);

        return user;
    }
}
