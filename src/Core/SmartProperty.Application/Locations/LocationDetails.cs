namespace SmartProperty.Application.Locations;

/// <summary>One place. <paramref name="Id"/> is this platform's id; it is what clients send to choose a place.</summary>
public sealed record LocationDetails(Guid Id, string NameEn, string NameAr);
