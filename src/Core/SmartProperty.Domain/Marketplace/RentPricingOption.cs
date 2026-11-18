namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// One way a rental is priced: an amount charged per pricing period, in its own currency.
/// </summary>
/// <remarks>
/// The pricing period (what the amount is charged per) is not the rental duration (how long a customer stays): a
/// 10-day stay priced per day pays ten daily amounts. An owner offers any of Daily, Weekly, Monthly and Yearly, each
/// with its own amount and currency. Immutable and compared by value.
/// </remarks>
public sealed record RentPricingOption
{
    public RentPricingOption(RentPeriod period, decimal amount, string currency, decimal? deposit = null)
    {
        if (!Enum.IsDefined(period))
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Pricing period is not supported.");
        }

        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A pricing option needs an amount above zero.");
        }

        // Exactly three upper-case ASCII letters, so a stored currency always matches what public search compares.
        if (currency is null || currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException("A currency is exactly three upper-case letters.", nameof(currency));
        }

        if (deposit is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(deposit), deposit, "A security deposit cannot be negative.");
        }

        Period = period;
        Amount = amount;
        Currency = currency;
        Deposit = deposit;
    }

    public RentPeriod Period { get; }
    public decimal Amount { get; }
    public string Currency { get; }

    /// <summary>The security deposit asked with this option, in its currency; null when none was stated.</summary>
    public decimal? Deposit { get; }

    /// <summary>
    /// The option that stands for the rental where a single price is needed (the plain listing price, a search
    /// index with one price per record): monthly, then yearly, weekly, daily.
    /// </summary>
    public static RentPricingOption PrimaryOf(IReadOnlyCollection<RentPricingOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var period in new[] { RentPeriod.Monthly, RentPeriod.Yearly, RentPeriod.Weekly, RentPeriod.Daily })
        {
            var match = options.FirstOrDefault(option => option.Period == period);

            if (match is not null)
            {
                return match;
            }
        }

        throw new ArgumentException("A rental needs at least one pricing option.", nameof(options));
    }
}
