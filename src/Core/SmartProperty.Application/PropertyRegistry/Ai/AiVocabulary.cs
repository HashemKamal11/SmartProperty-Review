using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Ai;

/// <summary>
/// Translates this platform's enum values to the strings of the AI search service's closed lists
/// (<c>/v1/options</c>). The names differ where the service uses spaces or symbols.
/// </summary>
public static class AiVocabulary
{
    public static string ToAi(PropertyType type)
    {
        return type switch
        {
            PropertyType.TwinHouse => "Twin House",
            PropertyType.StandaloneVilla => "Standalone Villa",
            PropertyType.HotelApartment => "Hotel Apartment",
            _ => Named(type)
        };
    }

    public static string ToAi(FinishingStatus status)
    {
        return status switch
        {
            FinishingStatus.Finished => "Finished",
            FinishingStatus.LuxuryFinished => "Luxury Finished",
            FinishingStatus.SemiFinished => "Semi Finished",
            FinishingStatus.CoreAndShell => "Core & Shell",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Finishing status is not supported.")
        };
    }

    /// <summary>The AI service names the two purposes Sale and Rent, like this platform.</summary>
    public static string ToAi(ListingPurpose purpose)
    {
        return Named(purpose);
    }


    private static string Named<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value is not supported.");
        }

        return value.ToString();
    }
}
