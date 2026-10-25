#nullable enable
namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// An amount and the currency it is expressed in, as shown on a public listing.
/// </summary>
/// <remarks>
/// PROVISIONAL contract field. The commercial terms behind a listing are not modelled yet, so this record only
/// fixes the public JSON shape. It carries plain values and has no dependency on the Domain or Application layers.
/// </remarks>
public sealed record PublicMoneyResponse(decimal Amount, string Currency);
