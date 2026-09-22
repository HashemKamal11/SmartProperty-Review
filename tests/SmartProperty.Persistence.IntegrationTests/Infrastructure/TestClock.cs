using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// A clock the test sets. Nothing here reads the machine's time, so no assertion can depend on how long a test
/// took to run.
/// </summary>
internal sealed class TestClock : IDateTimeProvider
{
    /// <summary>
    /// A fixed instant with whole-second precision, so a value written to a PostgreSQL <c>timestamptz</c> and
    /// read back compares equal without depending on the server's microsecond rounding.
    /// </summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = DefaultNow;
}
