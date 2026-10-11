#nullable enable
namespace SmartProperty.Api.Contracts.PropertyRegistry;

/// <summary>
/// One canonical property, as returned by the read endpoints. The JSON shape intentionally matches
/// <see cref="CreatePropertyResponse"/>; the types are kept separate so each contract can evolve on its own.
/// </summary>
public sealed record PropertyResponse(
    Guid Id,
    string Type,
    string Status,
    PropertyAddressResponse Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
