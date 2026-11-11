namespace SmartProperty.Domain.Marketplace;

/// <summary>Validation shared by purchase and rental requests.</summary>
internal static class RequestTerms
{
    public static void ValidateMoney(decimal amount, string currency)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "An amount above zero is required.");
        }

        if (currency is null || currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException("A currency is exactly three upper-case letters.", nameof(currency));
        }
    }

    public static void ValidateMessage(string? message, int maxLength)
    {
        if (message is not null && message.Trim().Length > maxLength)
        {
            throw new ArgumentException($"A message is at most {maxLength} characters.", nameof(message));
        }
    }

    public static void ValidateTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 64)
        {
            throw new ArgumentException("A time zone is required.", nameof(timeZoneId));
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("The time zone is not recognised.", nameof(timeZoneId));
        }
    }
}
