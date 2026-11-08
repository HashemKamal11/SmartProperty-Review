#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>
/// What a share action needs for one active property: the link to open it and a short preview. <c>Url</c> is absolute
/// when the server knows the public site address and is omitted otherwise; <c>Path</c> is always present.
/// </summary>
public sealed record PropertyShareResponse(
    Guid PropertyId,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl);
