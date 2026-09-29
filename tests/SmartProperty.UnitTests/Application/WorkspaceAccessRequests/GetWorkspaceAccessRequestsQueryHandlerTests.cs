using SmartProperty.Application.WorkspaceAccessRequests.List;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Workspaces;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.WorkspaceAccessRequests;

/// <summary>
/// Covers what the review queue asks the repository for: the default status, the optional workspace filter, and
/// the page window it turns caller input into.
/// </summary>
public sealed class GetWorkspaceAccessRequestsQueryHandlerTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeWorkspaceAccessRequestRepository _accessRequests = new();

    [Fact]
    public async Task AbsentStatus_DefaultsToPending()
    {
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(null, null, null, null));

        Assert.Equal(WorkspaceAccessRequestStatus.Pending, _accessRequests.LastListRequest!.Status);
    }

    [Theory]
    [InlineData(WorkspaceAccessRequestStatus.Approved)]
    [InlineData(WorkspaceAccessRequestStatus.Rejected)]
    public async Task ExplicitStatus_IsPassedThrough(WorkspaceAccessRequestStatus status)
    {
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(null, status, null, null));

        Assert.Equal(status, _accessRequests.LastListRequest!.Status);
    }

    [Fact]
    public async Task WorkspaceFilter_IsPassedThrough()
    {
        var workspaceId = Guid.NewGuid();
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(workspaceId, null, null, null));

        Assert.Equal(workspaceId, _accessRequests.LastListRequest!.WorkspaceId);
    }

    [Fact]
    public async Task EmptyWorkspaceFilter_IsTreatedAsNoFilter()
    {
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(Guid.Empty, null, null, null));

        // An empty id would match nothing at all, which is never what a caller means by it.
        Assert.Null(_accessRequests.LastListRequest!.WorkspaceId);
    }

    [Fact]
    public async Task AbsentPaging_UsesThePageParametersDefaults()
    {
        var defaults = new PageParameters();
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(null, null, null, null));

        Assert.Equal(0, _accessRequests.LastListRequest!.Skip);
        Assert.Equal(defaults.PageSize, _accessRequests.LastListRequest.Take);
    }

    [Fact]
    public async Task ExplicitPaging_BecomesTheMatchingWindow()
    {
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(null, null, 3, 20));

        Assert.Equal(40, _accessRequests.LastListRequest!.Skip);
        Assert.Equal(20, _accessRequests.LastListRequest.Take);
    }

    [Fact]
    public async Task AnAbsurdPageSize_IsClampedBeforeReachingTheRepository()
    {
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        await handler.Handle(new GetWorkspaceAccessRequestsQuery(null, null, -5, int.MaxValue));

        // PageParameters owns the bounds; nothing unbounded may reach the database.
        Assert.Equal(0, _accessRequests.LastListRequest!.Skip);
        Assert.Equal(PageParameters.MaxPageSize, _accessRequests.LastListRequest.Take);
    }

    [Fact]
    public async Task ExtremePaging_IsBoundedWithoutOffsetOverflow()
    {
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        var result = await handler.Handle(
            new GetWorkspaceAccessRequestsQuery(null, null, int.MaxValue, int.MaxValue));

        Assert.True(result.IsSuccess);
        Assert.Equal(PageParameters.MaxOffset, _accessRequests.LastListRequest!.Skip);
        Assert.True(_accessRequests.LastListRequest.Skip >= 0);
        Assert.Equal(PageParameters.MaxPageSize, _accessRequests.LastListRequest.Take);
        Assert.Equal(PageParameters.MaxPageNumber, result.Value.PageNumber);
        Assert.Equal(PageParameters.MaxPageSize, result.Value.PageSize);
    }

    [Fact]
    public async Task PageBeyondTheLast_ReturnsASuccessfulEmptyPageWithNormalizedMetadata()
    {
        _accessRequests.ListTotalCount = 1;
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        var result = await handler.Handle(
            new GetWorkspaceAccessRequestsQuery(null, null, int.MaxValue, 100));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.Equal(PageParameters.MaxPageNumber, result.Value.PageNumber);
        Assert.Equal(100, result.Value.PageSize);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.Equal(1, result.Value.TotalPages);
        Assert.False(result.Value.HasNextPage);
        Assert.True(result.Value.HasPreviousPage);
    }

    [Fact]
    public async Task TheRepositoryPage_BecomesThePagedResult()
    {
        _accessRequests.ListItems.Add(ListItem());
        _accessRequests.ListTotalCount = 41;
        var handler = new GetWorkspaceAccessRequestsQueryHandler(_accessRequests);

        var result = await handler.Handle(new GetWorkspaceAccessRequestsQuery(null, null, 2, 20));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Equal(2, result.Value.PageNumber);
        Assert.Equal(20, result.Value.PageSize);
        Assert.Equal(41, result.Value.TotalCount);
        Assert.Equal(3, result.Value.TotalPages);
        Assert.True(result.Value.HasNextPage);
        Assert.True(result.Value.HasPreviousPage);
    }

    private static WorkspaceAccessRequestListItem ListItem()
    {
        return new WorkspaceAccessRequestListItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "applicant@example.test",
            "Applicant",
            "User",
            Guid.NewGuid(),
            "Test Workspace",
            WorkspaceAccessRequestStatus.Pending,
            RequestedAt,
            null,
            null);
    }
}
