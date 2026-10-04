using SmartProperty.Application.Workspaces.RegistrationOptions;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Workspaces;

/// <summary>
/// The handler adds no rule of its own: it asks the repository once and hands its answer back unchanged. Ordering
/// and projection are the repository's, and are asserted against real PostgreSQL in the persistence suite.
/// </summary>
public sealed class GetRegistrationWorkspaceOptionsQueryHandlerTests
{
    private readonly FakeWorkspaceRepository _workspaces = new();

    [Fact]
    public async Task NoWorkspaces_SucceedsWithAnEmptyList()
    {
        var handler = new GetRegistrationWorkspaceOptionsQueryHandler(_workspaces);

        var result = await handler.Handle(new GetRegistrationWorkspaceOptionsQuery());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task ReturnsTheRepositoryOptionsUnchangedAndInOrder()
    {
        var first = new RegistrationWorkspaceOption(Guid.NewGuid(), "Alpha");
        var second = new RegistrationWorkspaceOption(Guid.NewGuid(), "Beta");
        _workspaces.RegistrationOptions.AddRange([first, second]);
        var handler = new GetRegistrationWorkspaceOptionsQueryHandler(_workspaces);

        var result = await handler.Handle(new GetRegistrationWorkspaceOptionsQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal([first, second], result.Value);
        Assert.Equal(1, _workspaces.ListRegistrationOptionsCallCount);
    }

    [Fact]
    public async Task PassesTheCancellationTokenToTheRepository()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new GetRegistrationWorkspaceOptionsQueryHandler(_workspaces);

        await handler.Handle(new GetRegistrationWorkspaceOptionsQuery(), cancellation.Token);

        Assert.Equal(cancellation.Token, _workspaces.LastCancellationToken);
    }

    [Fact]
    public async Task NullQuery_Throws()
    {
        var handler = new GetRegistrationWorkspaceOptionsQueryHandler(_workspaces);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.Handle(null!));
    }
}
