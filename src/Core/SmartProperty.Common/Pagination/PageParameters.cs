#nullable enable
namespace SmartProperty.Common.Pagination;

public sealed class PageParameters
{
    private int _pageNumber = 1;
    private int _pageSize = 10;

    /// <summary>
    /// Caps offset pagination below one million discarded rows when paired with <see cref="MaxPageSize"/>.
    /// Deeper traversal needs a cursor-based contract rather than an increasingly expensive SQL offset.
    /// </summary>
    public const int MaxPageNumber = 10_000;

    public const int MaxPageSize = 100;

    public const int MaxOffset = (MaxPageNumber - 1) * MaxPageSize;

    public PageParameters()
    {
    }

    public PageParameters(int pageNumber = 1, int pageSize = 10)
    {
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public int PageNumber
    {
        get => _pageNumber;
        set
        {
            if (value < 1) _pageNumber = 1;
            else if (value > MaxPageNumber) _pageNumber = MaxPageNumber;
            else _pageNumber = value;
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value < 1) _pageSize = 1;
            else if (value > MaxPageSize) _pageSize = MaxPageSize;
            else _pageSize = value;
        }
    }

    /// <summary>The zero-based row offset represented by the normalized page values.</summary>
    public int Offset => checked((PageNumber - 1) * PageSize);
}
