using System.Text.Json;
using System.Text.Json.Serialization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

/// <summary>
/// How a request stores the listing content the requester entered: their validated input, as JSON with enum names, so
/// the stored text is readable and round-trips exactly into the listing created on the final approval.
/// </summary>
internal static class AuthorityListingContent
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ListingDraftContentInput content) => JsonSerializer.Serialize(content, Options);

    /// <summary>The stored input, or null when nothing (or something unreadable) is stored.</summary>
    public static ListingDraftContentInput? Deserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ListingDraftContentInput>(stored, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
