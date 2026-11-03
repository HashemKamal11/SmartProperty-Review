using SmartProperty.Application.Workspaces.Administration.Get;
using SmartProperty.Application.Workspaces.Administration.List;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Workspaces;

public sealed class AdminWorkspaceQueryHandlerTests
{
    [Theory]
    [InlineData(null, null, 0, 10)]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(2, 5, 5, 5)]
    [InlineData(int.MaxValue, int.MaxValue, PageParameters.MaxOffset, 100)]
    public async Task PaginationIsBounded(int? page, int? size, int skip, int take)
    {
        var repository = new FakeWorkspaceRepository();
        using var cancellation = new CancellationTokenSource();
        var result = await new GetAdminWorkspacesQueryHandler(repository).Handle(new(page, size), cancellation.Token);
        Assert.True(result.IsSuccess);
        Assert.Equal(skip, repository.LastSkip);
        Assert.Equal(take, repository.LastTake);
        Assert.Equal(cancellation.Token, repository.LastCancellationToken);
    }

    [Fact]
    public async Task DetailReturnsTimestampsOrAdministrative404()
    {
        var repository = new FakeWorkspaceRepository();
        var handler = new GetAdminWorkspaceQueryHandler(repository);
        Assert.Equal("workspace_admin.not_found", (await handler.Handle(new(Guid.Empty))).Error!.Code);
        Assert.Equal(0, repository.GetByIdCallCount);
        Assert.Equal("workspace_admin.not_found", (await handler.Handle(new(Guid.NewGuid()))).Error!.Code);
        var workspace = new Workspace(Guid.NewGuid(), "Name", DateTimeOffset.UtcNow);
        await repository.AddAsync(workspace);
        var result = await handler.Handle(new(workspace.Id));
        Assert.Equal(workspace.Id, result.Value.Id);
        Assert.Equal(workspace.CreatedAt, result.Value.CreatedAt);
    }
}