using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// A clock the test sets. Never reads the machine clock, so every time-dependent assertion is exact rather
/// than approximate, and no test depends on how long it took to run.
/// </summary>
internal sealed class FakeDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
{
    private DateTimeOffset utcNow = utcNow;

    public int UtcNowCallCount { get; private set; }

    public DateTimeOffset UtcNow
    {
        get
        {
            UtcNowCallCount++;
            return utcNow;
        }
        set => utcNow = value;
    }
}
