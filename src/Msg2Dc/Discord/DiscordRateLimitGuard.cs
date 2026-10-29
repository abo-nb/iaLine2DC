using Discord;
using MongoDB.Bson;
using MongoDB.Driver;
using Msg2Dc.Storage;

namespace Msg2Dc.Discord;

/// <summary>
/// Remembers Discord's retry-after and keeps it across restarts. Discord.Net retries a
/// rate-limited request in a tight loop, which turns one 429 into hundreds of requests
/// and an hour-long ban; this guard lets the bot stop the client instead.
/// </summary>
public sealed class DiscordRateLimitGuard
{
    private const string StateKey = "discord-rate-limit";

    // A Retry-After header only appears on 429s, but be defensive: ignore tiny values
    // (normal bucket hints) and absurd ones.
    private const double MinimumRetryAfterSeconds = 2;
    private const double MaximumRetryAfterSeconds = 24 * 60 * 60;

    private readonly MongoStore _store;
    private readonly ILogger<DiscordRateLimitGuard> _log;
    private readonly RateLimitState _state = new();

    public DiscordRateLimitGuard(MongoStore store, ILogger<DiscordRateLimitGuard> log)
    {
        _store = store;
        _log = log;
    }

    public DateTimeOffset BlockedUntil => _state.BlockedUntil;

    public double LastRetryAfterSeconds => _state.LastRetryAfterSeconds;

    public bool IsBlocked => _state.IsBlocked;

    /// <summary>Completes when a new block is recorded; used to wake the supervisor.</summary>
    public Task Signal => _state.Signal;

    public void Report(IRateLimitInfo? info)
    {
        var seconds = info?.RetryAfter ?? 0;
        if (seconds < MinimumRetryAfterSeconds || seconds > MaximumRetryAfterSeconds)
        {
            return;
        }

        if (!_state.Report(seconds))
        {
            return;
        }

        var until = _state.BlockedUntil;
        _log.LogWarning(
            "Discord REST rate limit hit; the client will stay offline until {Until:u} ({Seconds:F0}s)",
            until, seconds);

        _ = PersistAsync(until);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var doc = await _store.SystemStates
                .Find(Builders<SystemStateDoc>.Filter.Eq(s => s.Key, StateKey))
                .FirstOrDefaultAsync(ct);

            if (doc is null)
            {
                return;
            }

            var until = new DateTimeOffset(DateTime.SpecifyKind(doc.BlockedUntil, DateTimeKind.Utc));
            if (_state.Restore(until))
            {
                _log.LogWarning(
                    "Discord REST is still rate limited until {Until:u}; REST writes will be skipped",
                    until);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the persisted Discord rate-limit state");
        }
    }

    private async Task PersistAsync(DateTimeOffset until)
    {
        try
        {
            await _store.SystemStates.UpdateOneAsync(
                Builders<SystemStateDoc>.Filter.Eq(s => s.Key, StateKey),
                Builders<SystemStateDoc>.Update
                    .Set(s => s.BlockedUntil, until.UtcDateTime)
                    .Set(s => s.UpdatedAt, DateTime.UtcNow)
                    .SetOnInsert(s => s.Id, ObjectId.GenerateNewId()),
                new UpdateOptions { IsUpsert = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not persist the Discord rate-limit state");
        }
    }
}
