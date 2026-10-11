namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// One page of canonical properties together with the total number of rows the filter matches.
/// </summary>
/// <remarks>
/// Returned as a pair so the page and its total come from one repository call, rather than leaving a caller to
/// remember to ask for the count separately and risk the two disagreeing.
/// </remarks>
public sealed record PropertyPage(
    IReadOnlyList<PropertyDetails> Items,
    long TotalCount);
