#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartProperty.Common.Pagination;

public sealed class PagedList<T>
{
    public IReadOnlyList<T> Items { get; }
    public int PageNumber { get; }
    public int PageSize { get; }
    public long TotalCount { get; }
    public int TotalPages { get; }
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    private PagedList(IReadOnlyList<T> items, int pageNumber, int pageSize, long totalCount)
    {
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalCount = totalCount;
        TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
    }

    public static PagedList<T> Create(IReadOnlyList<T> items, int pageNumber, int pageSize, long totalCount)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (totalCount < 0) throw new ArgumentOutOfRangeException(nameof(totalCount), "totalCount must be >= 0");

        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > PageParameters.MaxPageSize) pageSize = PageParameters.MaxPageSize;

        // Defensive copy to create an immutable snapshot
        var snapshot = items as T[] ?? items.ToArray();

        return new PagedList<T>(snapshot, pageNumber, pageSize, totalCount);
    }
}
