namespace SmartProperty.Domain.PropertyRegistry;

/// <summary>
/// The market-facing facts about a property: what is asked for it, how big it is, and how it is presented. All of
/// them are optional in the registry, but the AI search service needs the price, area, transaction type, and place
/// before it can hold the property, so the property reports what is still missing.
/// </summary>
/// <remarks>
/// Immutable and compared by value, like <see cref="PropertyAddress"/>. Currency is not held here: it follows the
/// country of the chosen area on the AI side.
/// </remarks>
public sealed record PropertyListingInfo
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 10000;
    public const decimal MaxAreaSqm = 100000m;
    public const int MinFloor = -5;
    public const int MaxFloor = 300;

    public static readonly PropertyListingInfo Empty = new();

    private PropertyListingInfo()
    {
        ListingStatus = ListingStatus.Available;
    }

    public PropertyListingInfo(
        TransactionType? transactionType = null,
        decimal? price = null,
        decimal? areaSqm = null,
        int? floor = null,
        bool? furnished = null,
        ListingStatus listingStatus = ListingStatus.Available,
        string? titleAr = null,
        string? titleEn = null,
        string? descriptionAr = null,
        string? descriptionEn = null)
    {
        if (transactionType is { } transaction && !Enum.IsDefined(transaction))
        {
            throw new ArgumentOutOfRangeException(
                nameof(transactionType), transactionType, "Transaction type is not supported.");
        }

        if (price is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "Price must be greater than zero.");
        }

        if (areaSqm is <= 0m or > MaxAreaSqm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(areaSqm), areaSqm, $"Area must be greater than zero and at most {MaxAreaSqm} square metres.");
        }

        if (floor is < MinFloor or > MaxFloor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(floor), floor, $"Floor must be between {MinFloor} and {MaxFloor}.");
        }

        if (!Enum.IsDefined(listingStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(listingStatus), listingStatus, "Listing status is not supported.");
        }

        TransactionType = transactionType;
        Price = price;
        AreaSqm = areaSqm;
        Floor = floor;
        Furnished = furnished;
        ListingStatus = listingStatus;
        TitleAr = TrimOptional(titleAr, MaxTitleLength, nameof(titleAr));
        TitleEn = TrimOptional(titleEn, MaxTitleLength, nameof(titleEn));
        DescriptionAr = TrimOptional(descriptionAr, MaxDescriptionLength, nameof(descriptionAr));
        DescriptionEn = TrimOptional(descriptionEn, MaxDescriptionLength, nameof(descriptionEn));
    }

    public TransactionType? TransactionType { get; private set; }
    public decimal? Price { get; private set; }
    public decimal? AreaSqm { get; private set; }
    public int? Floor { get; private set; }
    public bool? Furnished { get; private set; }
    public ListingStatus ListingStatus { get; private set; }
    public string? TitleAr { get; private set; }
    public string? TitleEn { get; private set; }
    public string? DescriptionAr { get; private set; }
    public string? DescriptionEn { get; private set; }

    private static string? TrimOptional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return trimmed;
    }
}
