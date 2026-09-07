#nullable enable
namespace SmartProperty.Common.Pagination;

public sealed class PageParameters
{
    private int _pageNumber = 1;
    private int _pageSize = 10;

    public const int MaxPageSize = 100;

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
        set => _pageNumber = (value < 1) ? 1 : value;
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
}
