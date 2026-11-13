namespace Msg2Dc.Verification;

/// <summary>
/// Cheap per-IP protection for the verification endpoints: a legitimate caller needs a
/// handful of requests per minute, so a burst far above that is an enumeration attempt.
/// Bans live in memory only — a restart clears them.
/// </summary>
public sealed class AbuseGuard
{
    // 300/min ≈ 5/s: a whole class verifying at once (request + confirm + me) stays well
    // under it, while a scan does thousands.
    private const int LimitPerWindow = 300;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BanFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeepEntry = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ILogger<AbuseGuard> _log;
    private DateTime _lastPrune = DateTime.UtcNow;

    public AbuseGuard(ILogger<AbuseGuard> log)
    {
        _log = log;
    }

    /// <summary>
    /// Counts this request and returns whether the caller is (now) banned, plus how many
    /// seconds the ban still lasts.
    /// </summary>
    public (bool Banned, int RetryAfterSeconds) Check(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return (false, 0);
        }

        var now = DateTime.UtcNow;

        lock (_gate)
        {
            Prune(now);

            if (!_entries.TryGetValue(ip, out var entry))
            {
                entry = new Entry { WindowStart = now };
                _entries[ip] = entry;
            }

            entry.LastSeen = now;

            if (entry.BannedUntil > now)
            {
                return (true, (int)Math.Ceiling((entry.BannedUntil - now).TotalSeconds));
            }

            if (now - entry.WindowStart >= Window)
            {
                entry.WindowStart = now;
                entry.Count = 0;
            }

            entry.Count++;

            if (entry.Count > LimitPerWindow)
            {
                entry.BannedUntil = now + BanFor;
                entry.Count = 0;
                entry.WindowStart = now;
                _log.LogWarning(
                    "Blocked {Ip} for {Minutes} min after more than {Limit} verification requests in a minute",
                    ip, BanFor.TotalMinutes, LimitPerWindow);
                return (true, (int)BanFor.TotalSeconds);
            }
        }

        return (false, 0);
    }

    private void Prune(DateTime now)
    {
        if (now - _lastPrune < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastPrune = now;

        foreach (var key in _entries.Where(pair => now - pair.Value.LastSeen > KeepEntry).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public DateTime WindowStart { get; set; }
        public int Count { get; set; }
        public DateTime BannedUntil { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
