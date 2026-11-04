using System.Collections.Concurrent;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Application.Authentication.Login;

/// <summary>
/// <see cref="ILoginAttemptLimiter"/> kept in process memory: <see cref="MaxFailures"/> failed attempts within
/// <see cref="Window"/> lock the email for <see cref="LockoutDuration"/>.
/// </summary>
/// <remarks>
/// Memory is bounded: at <see cref="MaxTrackedEmails"/> entries expired ones are dropped, and if the table is still
/// full new emails are simply not tracked (the per-IP limiter still applies) rather than growing without limit.
/// </remarks>
public sealed class InMemoryLoginAttemptLimiter(IDateTimeProvider dateTimeProvider) : ILoginAttemptLimiter
{
    public const int MaxFailures = 10;
    public const int MaxTrackedEmails = 50_000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsLockedOut(string email)
    {
        if (!_entries.TryGetValue(Key(email), out var entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.LockedUntil is { } until && dateTimeProvider.UtcNow < until;
        }
    }

    public void RegisterFailure(string email)
    {
        var key = Key(email);
        var now = dateTimeProvider.UtcNow;

        if (!_entries.TryGetValue(key, out var entry))
        {
            if (_entries.Count >= MaxTrackedEmails)
            {
                Prune(now);

                if (_entries.Count >= MaxTrackedEmails)
                {
                    return;
                }
            }

            entry = _entries.GetOrAdd(key, _ => new Entry(now));
        }

        lock (entry)
        {
            if (entry.LockedUntil is { } until && now < until)
            {
                return;
            }

            if (now - entry.WindowStart >= Window || entry.LockedUntil is not null)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
                entry.LockedUntil = null;
            }

            entry.Failures++;

            if (entry.Failures >= MaxFailures)
            {
                entry.LockedUntil = now.Add(LockoutDuration);
            }
        }
    }

    public void Reset(string email)
    {
        _entries.TryRemove(Key(email), out _);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            bool expired;

            lock (pair.Value)
            {
                expired = pair.Value.LockedUntil is { } until
                    ? now >= until
                    : now - pair.Value.WindowStart >= Window;
            }

            if (expired)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    // The same normalization User applies to a stored email, so case and padding cannot be used to dodge the limit.
    private static string Key(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private sealed class Entry(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;
        public int Failures { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
