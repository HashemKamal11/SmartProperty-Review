#nullable enable
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.Contracts;

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages,
    bool HasNext,
    bool HasPrevious)
{
    public static PagedResponse<T> FromPagedList(PagedList<T> pagedList)
    {
        ArgumentNullException.ThrowIfNull(pagedList);

        return new PagedResponse<T>(
            pagedList.Items,
            pagedList.PageNumber,
            pagedList.PageSize,
            pagedList.TotalCount,
            pagedList.TotalPages,
            pagedList.HasNextPage,
            pagedList.HasPreviousPage);
    }
}
