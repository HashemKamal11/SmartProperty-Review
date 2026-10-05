using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authentication.Context;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

public sealed class GetAuthContextQueryHandlerTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeAccessContextReader _reader = new();

    [Fact]
    public async Task NoSubjectReturnsUnauthorizedWithoutReadingPersistence()
    {
        var handler = CreateHandler(userId: null);

        var result = await handler.Handle(new GetAuthContextQuery());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.Unauthorized, result.Error);
        Assert.Equal(0, _users.GetByIdCallCount);
        Assert.Equal(0, _reader.ReadCallCount);
    }

    [Fact]
    public async Task MissingUserReturnsUnauthorizedWithoutReadingAccessContext()
    {
        var handler = CreateHandler(Guid.NewGuid());

        var result = await handler.Handle(new GetAuthContextQuery());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.Unauthorized, result.Error);
        Assert.Equal(1, _users.GetByIdCallCount);
        Assert.Equal(0, _reader.ReadCallCount);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUserReturnsAccountUnavailableWithoutReadingAccessContext(UserStatus status)
    {
        var user = SeedUser(status);
        var handler = CreateHandler(user.Id);

        var result = await handler.Handle(new GetAuthContextQuery());

        Assert.True(result.IsFailure);
        Assert.Same(MeErrors.AccountUnavailable, result.Error);
        Assert.Equal(0, _reader.ReadCallCount);
    }

    [Fact]
    public async Task ActiveUserReturnsProfileAndEffectiveAccessContext()
    {
        var user = SeedUser(UserStatus.Active);
        var workspace = new WorkspaceAccessContext(
            Guid.NewGuid(),
            "Workspace",
            ["Manager"],
            ["property.read"]);
        _reader.Context = new AccessContext(
            ["Platform Admin"],
            ["property.create"],
            [workspace]);
        var handler = CreateHandler(user.Id);

        var result = await handler.Handle(new GetAuthContextQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value.User.Id);
        Assert.Equal(user.Email, result.Value.User.Email);
        Assert.Equal(user.FirstName, result.Value.User.FirstName);
        Assert.Equal(user.LastName, result.Value.User.LastName);
        Assert.Equal(["Platform Admin"], result.Value.PlatformRoles);
        Assert.Equal(["property.create"], result.Value.PlatformPermissions);
        var returnedWorkspace = Assert.Single(result.Value.Workspaces);
        Assert.Equal(workspace.Id, returnedWorkspace.Id);
        Assert.Equal(workspace.Name, returnedWorkspace.Name);
        Assert.Equal(workspace.Roles, returnedWorkspace.Roles);
        Assert.Equal(workspace.Permissions, returnedWorkspace.Permissions);
        Assert.Equal(1, _reader.ReadCallCount);
        Assert.Equal(user.Id, _reader.LastUserId);
    }

    [Fact]
    public async Task PassesCancellationTokenToAccessReader()
    {
        var user = SeedUser(UserStatus.Active);
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(user.Id);

        await handler.Handle(new GetAuthContextQuery(), cancellation.Token);

        Assert.Equal(cancellation.Token, _reader.LastCancellationToken);
    }

    [Fact]
    public async Task NullQueryThrows()
    {
        var handler = CreateHandler(Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.Handle(null!));
    }

    private GetAuthContextQueryHandler CreateHandler(Guid? userId)
    {
        return new GetAuthContextQueryHandler(new FakeCurrentUser(userId), _users, _reader);
    }

    private User SeedUser(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), "context@example.test", "Context", "User", CreatedAt);

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
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status.");
        }

        _users.Seed(user);
        return user;
    }
}
