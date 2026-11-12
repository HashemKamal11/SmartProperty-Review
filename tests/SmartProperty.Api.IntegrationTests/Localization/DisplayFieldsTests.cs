using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Localization;
using SmartProperty.Common.Localization;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Localization;

/// <summary>
/// The display fields the serializer adds to every response object, in each language, without a host or a database.
/// </summary>
public sealed class DisplayFieldsTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    [Theory]
    [InlineData("ar", "مصر")]
    [InlineData("en", "Egypt")]
    public void AnEnglishArabicPairGetsADisplayFieldInTheRequestedLanguage(string language, string expected)
    {
        var json = Serialize(new SamplePlace(Guid.Empty, "Egypt", "مصر"), language);

        Assert.Equal(expected, json.GetProperty("displayName").GetString());
        Assert.Equal("Egypt", json.GetProperty("nameEn").GetString());
        Assert.Equal("مصر", json.GetProperty("nameAr").GetString());
        Assert.Equal(["id", "nameEn", "nameAr", "displayName"], Names(json));
    }

    [Fact]
    public void AMissingTranslationFallsBackToTheOtherLanguage()
    {
        var json = Serialize(new SampleTitle("Sea view flat", null), "ar");

        Assert.Equal("Sea view flat", json.GetProperty("displayTitle").GetString());
    }

    [Fact]
    public void AFieldWithAnArabicSiblingIsShownFromItRatherThanFromALabel()
    {
        var json = Serialize(new SampleSearchItem("Cairo", "القاهرة", "Apartment", "شقة سكنية"), "ar");

        Assert.Equal("القاهرة", json.GetProperty("displayCity").GetString());
        Assert.Equal("شقة سكنية", json.GetProperty("displayPropertyType").GetString());
        Assert.Equal(["city", "cityAr", "displayCity", "propertyType", "propertyTypeAr", "displayPropertyType"], Names(json));
    }

    [Theory]
    [InlineData("ar", "قيد المراجعة", "شقة", "نصف تشطيب", "بيع")]
    [InlineData("en", "Pending review", "Apartment", "Semi finished", "Sale")]
    public void ClosedListValuesGetTheirLabel(string language, string status, string type, string finishing, string purpose)
    {
        var json = Serialize(
            new SampleValues("PendingReview", "Apartment", "Semi Finished", "image/png", ["Sale", "Rent"], ListingPurpose.Sale),
            language);

        Assert.Equal(status, json.GetProperty("displayStatus").GetString());
        Assert.Equal(type, json.GetProperty("displayPropertyType").GetString());
        Assert.Equal(finishing, json.GetProperty("displayFinishingStatus").GetString());
        Assert.Equal(purpose, json.GetProperty("displayPurposes")[0].GetString());
        Assert.Equal(purpose, json.GetProperty("displayListingPurpose").GetString());
        Assert.False(json.TryGetProperty("displayContentType", out _));
    }

    [Fact]
    public void AValueWithNoLabelIsShownAsWords()
    {
        var json = Serialize(new SampleValues("SomethingNew", "Apartment", null, "x", [], ListingPurpose.Rent), "en");

        Assert.Equal("Something new", json.GetProperty("displayStatus").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("displayFinishingStatus").ValueKind);
    }

    [Fact]
    public void ADisplayFieldIsOmittedWhenItsSourceIsOmittedForBeingNull()
    {
        var json = Serialize(new SampleOptional(null), "ar");

        Assert.False(json.TryGetProperty("pricingPeriod", out _));
        Assert.False(json.TryGetProperty("displayPricingPeriod", out _));

        var withValue = Serialize(new SampleOptional("Monthly"), "ar");
        Assert.Equal("شهري", withValue.GetProperty("displayPricingPeriod").GetString());
    }

    [Fact]
    public void AnOptedOutFieldGetsNoDisplayField()
    {
        var json = Serialize(new SampleOptedOut("Draft"), "ar");

        Assert.False(json.TryGetProperty("displayStatus", out _));
    }

    [Theory]
    [InlineData("Email is required.", "البريد الإلكتروني مطلوب.")]
    [InlineData("Email must not exceed 320 characters.", "يجب ألا يتجاوز البريد الإلكتروني 320 حرفًا.")]
    [InlineData("A message nobody translated.", "A message nobody translated.")]
    [InlineData("Still missing before submitting: Details, OwnershipProof.", "ما زال مطلوبًا قبل الإرسال: التفاصيل، إثبات الملكية.")]
    [InlineData(
        "The listing could not be published: the property is not available.",
        "تعذّر نشر الإعلان: العقار غير متاح.")]
    [InlineData("Unknown amenity code(s): Helipad, Moat.", "رموز غير معروفة: Helipad, Moat.")]
    public void AnErrorMessageIsTranslatedIncludingTheValuesOfATemplate(string message, string expected)
    {
        var json = Serialize(new ApiErrorResponse("validation.failed", message, 422, null, "c"), "ar");

        Assert.Equal(expected, json.GetProperty("displayMessage").GetString());
        Assert.Equal(message, json.GetProperty("message").GetString());
    }

    [Fact]
    public void AnErrorMessageStaysEnglishForEnglish()
    {
        var json = Serialize(new ApiErrorResponse("validation.failed", "Email is required.", 422, null, "c"), "en");

        Assert.Equal("Email is required.", json.GetProperty("displayMessage").GetString());
    }

    [Fact]
    public void RequestsIgnoreTheDisplayFields()
    {
        var place = JsonSerializer.Deserialize<SamplePlace>(
            """{"id":"00000000-0000-0000-0000-000000000000","nameEn":"Egypt","nameAr":"مصر","displayName":"x"}""",
            Options)!;

        Assert.Equal("Egypt", place.NameEn);
    }

    [Fact]
    public void EveryValueOfAnEnumClientsSeeHasAnEnglishAndArabicLabel()
    {
        // Enums that only drive server logic, sorting or error codes are never returned as values.
        var internalOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "PasswordVerificationStatus", "PersistenceConstraint", "PersistenceResource", "ListingWriteConflictKind",
            "ListingEligibilityDenial", "ErrorType", "PublicListingSortOrder", "PropertyListSortOrder", "OneTimeCodePurpose",
            "MalwareScanVerdict"
        };

        var enums = new[] { typeof(PropertyType).Assembly, typeof(SmartProperty.Application.Locations.LocationDetails).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsEnum && type.IsPublic && !internalOnly.Contains(type.Name));

        var missing = enums
            .SelectMany(type => Enum.GetNames(type).Select(name => $"{type.Name}.{name}"))
            .Where(name => !DisplayValues.IsKnown(name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..]))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryArabicMessageUsesOnlyThePlaceholdersOfItsEnglishTemplate()
    {
        using var stream = typeof(DisplayMessages).Assembly.GetManifestResourceStream(
            "SmartProperty.Api.Infrastructure.Localization.ArabicMessages.json")!;
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;

        // An Arabic sentence may leave out a word it does not need (an English noun), never add one.
        var broken = entries
            .Where(entry => !Placeholders(entry.Value).IsSubsetOf(Placeholders(entry.Key)))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.True(entries.Count > 500);
        Assert.Empty(broken);
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal), "\\{[^{}]+\\}")
            .Select(match => match.Value)];

    private static JsonElement Serialize<T>(T value, string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            return JsonSerializer.SerializeToElement(value, Options);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static string[] Names(JsonElement json) => [.. json.EnumerateObject().Select(property => property.Name)];

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        DisplayFieldsModifier.Apply(options);

        return options;
    }

    private sealed record SamplePlace(Guid Id, string NameEn, string NameAr);

    private sealed record SampleTitle(string? TitleEn, string? TitleAr);

    private sealed record SampleSearchItem(string? City, string? CityAr, string? PropertyType, string? PropertyTypeAr);

    private sealed record SampleValues(
        string Status,
        string PropertyType,
        string? FinishingStatus,
        string ContentType,
        IReadOnlyList<string> Purposes,
        [property: LocalizedValue] ListingPurpose ListingPurpose);

    private sealed record SampleOptional(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PricingPeriod);

    private sealed record SampleOptedOut([property: NotLocalized] string Status);
}
