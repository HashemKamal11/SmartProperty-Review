#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>
/// What a share action needs for one active property: the link to open it and a short preview. <c>Url</c> is always absolute
/// (the configured public site address, or the request origin when none is configured); <c>Path</c> is the relative part.
/// </summary>
public sealed record PropertyShareResponse(
    Guid PropertyId,
    string Path,
    string Url,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl);
