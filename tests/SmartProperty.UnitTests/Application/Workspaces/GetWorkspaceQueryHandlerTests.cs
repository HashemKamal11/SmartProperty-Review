using SmartProperty.Application.Workspaces.Get;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Workspaces;

/// <summary>
/// A workspace that is not there is a <c>Forbidden</c> failure, never a not-found, so the answer cannot be used to
/// learn whether a workspace exists.
/// </summary>
public sealed class GetWorkspaceQueryHandlerTests
{
    private readonly FakeWorkspaceRepository _workspaces = new();

    [Fact]
    public async Task AnExistingWorkspace_ReturnsItsIdAndName()
    {
        var workspace = new Workspace(Guid.NewGuid(), "Investor Workspace", DateTimeOffset.UtcNow);
        await _workspaces.AddAsync(workspace);
        var handler = new GetWorkspaceQueryHandler(_workspaces);

        var result = await handler.Handle(new GetWorkspaceQuery(workspace.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(new WorkspaceDetails(workspace.Id, "Investor Workspace"), result.Value);
    }

    [Fact]
    public async Task AMissingWorkspace_FailsAsForbiddenAndNeverAsNotFound()
    {
        var handler = new GetWorkspaceQueryHandler(_workspaces);

        var result = await handler.Handle(new GetWorkspaceQuery(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(GetWorkspaceErrors.Forbidden, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
        Assert.NotEqual(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task AnEmptyId_FailsAsForbiddenWithoutQueryingTheRepository()
    {
        var handler = new GetWorkspaceQueryHandler(_workspaces);

        var result = await handler.Handle(new GetWorkspaceQuery(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Same(GetWorkspaceErrors.Forbidden, result.Error);
        Assert.Equal(0, _workspaces.GetByIdCallCount);
    }

    [Fact]
    public async Task NullQuery_Throws()
    {
        var handler = new GetWorkspaceQueryHandler(_workspaces);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.Handle(null!));
    }
}
