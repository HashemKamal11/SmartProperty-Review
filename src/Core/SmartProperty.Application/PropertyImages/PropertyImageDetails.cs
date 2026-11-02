namespace SmartProperty.Application.PropertyImages;

/// <summary>Read model of one property image. The first in a property's list (lowest sort order) is its cover.</summary>
public sealed record PropertyImageDetails(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    int SortOrder);
