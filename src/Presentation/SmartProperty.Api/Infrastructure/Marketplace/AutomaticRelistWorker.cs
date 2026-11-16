#nullable enable
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;

namespace SmartProperty.Api.Infrastructure.Marketplace;

/// <summary>The "AutomaticRelist" section.</summary>
internal sealed class AutomaticRelistOptions
{
    public const string SectionName = "AutomaticRelist";

    /// <summary>Run the pass on this instance. Default on; turn it off on every instance but one if several run.</summary>
    public bool WorkerEnabled { get; init; } = true;

    /// <summary>
    /// How often the pass runs: a whole number followed by m (minutes), h (hours) or d (days), for example "5m", "1h"
    /// or "1d", from 1 minute to 7 days. Counted from when the API starts. Default 15m. Empty means use
    /// <see cref="RunAt"/> instead.
    /// </summary>
    public string? Every { get; init; } = "15m";

    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// The local times of day the pass runs, used only when <see cref="Every"/> is empty: HH:mm, comma-separated for
    /// several runs a day (for example "01:00,13:00"). Default 01:00.
    /// </summary>
    public string RunAt { get; init; } = "01:00";

    /// <summary>
    /// Also run once <see cref="StartupDelay"/> after the API starts, to catch up on what became due while it was down.
    /// Default on.
    /// </summary>
    public bool RunOnStartup { get; init; } = true;

    /// <summary>Lets the API finish starting before the catch-up pass touches the database.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    public bool UsesInterval =>!string.IsNullOrWhiteSpace(Every);

    /// <summary>The interval, or null when <see cref="Every"/> is empty or not valid.</summary>
    public TimeSpan? ParseInterval()
    {
        var text = Every?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length < 2 || !int.TryParse(text[..^1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        TimeSpan? interval = char.ToLowerInvariant(text[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => null
        };

        return interval is { } value && value >= MinInterval && value <= MaxInterval ? value : null;
    }

    public const int MaxRunsPerDay = 24;

    /// <summary>The run times, earliest first without repeats, or null when <see cref="RunAt"/> is not valid.</summary>
    public IReadOnlyList<TimeOnly>? ParseRunTimes()
    {
        var parts = (RunAt ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var times = new List<TimeOnly>(parts.Length);

        foreach (var part in parts)
        {
            if (!TimeOnly.TryParseExact(part, "HH:mm", out var time))
            {
                return null;
            }

            times.Add(time);
        }

        var distinct = times.Distinct().Order().ToArray();

        return distinct.Length is >= 1 and <= MaxRunsPerDay ? distinct : null;
    }

    /// <summary>The IANA time zone <see cref="RunAt"/> is read in. Default Africa/Cairo.</summary>
    public string TimeZoneId { get; init; } = "Africa/Cairo";

    /// <summary>The most listings one pass relists. Whatever is left waits for the next night.</summary>
    public int BatchSize { get; init; } = 500;

    public bool IsValid()
    {
        return (UsesInterval ? ParseInterval() is not null : ParseRunTimes() is not null)
            && TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _)
            && BatchSize is >= 1 and <= 10_000;
    }
}

/// <summary>
/// Every configured interval (15 minutes by default), or at fixed times of day, submits for review a copy of every listing whose owner chose automatic relisting and whose accepted
/// request has ended without a lasting deal. Each listing is its own unit of work in its own scope, so one refusal
/// (a lapsed grant, a document no longer clean, passed dates) never stops the others. Nothing is published here: an
/// administrator reviews every copy.
/// </summary>
internal sealed class AutomaticRelistWorker(
    IServiceScopeFactory scopes,
    IOptions<AutomaticRelistOptions> options,
    IDateTimeProvider clock,
    ILogger<AutomaticRelistWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.WorkerEnabled)
        {
            logger.LogInformation("The automatic relisting worker is turned off on this instance.");
            return;
        }

        var interval = settings.UsesInterval ? settings.ParseInterval() : null;
        var runTimes = interval is null ? settings.ParseRunTimes()! : [];
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        logger.LogInformation(
            "Automatic relisting runs {Schedule}.",
            interval is not null ? $"every {settings.Every!.Trim()}" : $"daily at {settings.RunAt} ({settings.TimeZoneId})");

        // A catch-up pass shortly after start: anything that became due while the API was down or being redeployed is
        // handled now, and frequent restarts can never keep pushing a long schedule back.
        if (settings.RunOnStartup)
        {
            try
            {
                await Task.Delay(AutomaticRelistOptions.StartupDelay, stoppingToken);
                await RunOnceAsync(settings.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError("Automatic relisting start-up pass failed: {ExceptionType}. It runs again at the next scheduled time.", exception.GetType().Name);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            var delay = interval ?? runTimes.Min(runAt => NextRun(now, runAt, zone)) - now;

            try
            {
                await Task.Delay(delay, stoppingToken);
                await RunOnceAsync(settings.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Automatic relisting pass failed: {ExceptionType}. It runs again at the next scheduled time.", exception.GetType().Name);
            }
        }
    }

    /// <summary>The next moment, strictly after <paramref name="now"/>, that is <paramref name="runAt"/> in the zone.</summary>
    internal static DateTimeOffset NextRun(DateTimeOffset now, TimeOnly runAt, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date + runAt.ToTimeSpan();

        for (var day = 0; day < 3; day++, candidate = candidate.AddDays(1))
        {
            // A time skipped by a clock change does not exist that day; the pass waits for the next one.
            if (zone.IsInvalidTime(candidate))
            {
                continue;
            }

            var at = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));

            if (at > now)
            {
                return at;
            }
        }

        return now.AddDays(1);
    }

    internal async Task RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> due;

        using (var scope = scopes.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<IAutomaticRelistQueue>()
                .GetDueListingIdsAsync(clock.UtcNow, batchSize, cancellationToken);
        }

        int relisted = 0, refused = 0;

        foreach (var listingId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ListingRelister>()
                    .RelistAutomaticallyAsync(listingId, cancellationToken);

                if (result.IsFailure)
                {
                    refused++;
                    logger.LogInformation(
                        "Listing {ListingId} was not relisted automatically: {ErrorCode}.", listingId, result.Error!.Code);
                }
                else
                {
                    relisted++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                refused++;
                logger.LogError(
                    "Automatic relisting of listing {ListingId} failed: {ExceptionType}.", listingId, exception.GetType().Name);
            }
        }

        if (due.Count > 0)
        {
            logger.LogInformation(
                "Automatic relisting pass: {Relisted} submitted for review, {Refused} not relisted.", relisted, refused);
        }
    }
}

internal static class AutomaticRelistServiceCollectionExtensions
{
    public static IServiceCollection AddAutomaticRelisting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AutomaticRelistOptions>()
            .Bind(configuration.GetSection(AutomaticRelistOptions.SectionName))
            .Validate(options => options.IsValid(), "AutomaticRelist settings are out of range: Every is a number with m, h or d from 1m to 7d (or empty to use RunAt), RunAt is HH:mm or a comma-separated list of up to 24 such times, TimeZoneId an IANA zone, BatchSize 1 to 10000.")
            .ValidateOnStart();
        services.AddHostedService<AutomaticRelistWorker>();

        return services;
    }
}
