#nullable enable
using System.Text.Json.Serialization;

namespace SmartProperty.Api.Contracts.Marketplace.Public;

/// <summary>
/// An amount and the currency it is expressed in, as shown on a public listing. A rent price also states the period
/// it is charged for.
/// </summary>
/// <remarks>
/// PROVISIONAL contract field. The commercial terms behind a listing are not modelled yet, so this record only
/// fixes the public JSON shape. It carries plain values and has no dependency on the Domain or Application layers.
///
/// <see cref="RentPeriod"/> is exactly <c>"Monthly"</c> or <c>"Yearly"</c> and is present on the price of every Rent
/// listing. A Sale price has no rent period, and the member is left out of the JSON entirely rather than written as
/// null. No default is ever supplied and the period is never inferred from the amount.
/// </remarks>
public sealed record PublicMoneyResponse(
    decimal Amount,
    string Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RentPeriod = null);
