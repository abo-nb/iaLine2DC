namespace Msg2Dc.Discord;

/// <summary>
/// Tracks how long the Discord REST API has asked us to stay away.
/// Kept free of I/O so the behaviour can be unit tested.
/// </summary>
public sealed class RateLimitState
{
    private readonly object _lock = new();
    private TaskCompletionSource _signal = NewSignal();

    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private double _lastRetryAfterSeconds;

    public DateTimeOffset BlockedUntil
    {
        get { lock (_lock) return _blockedUntil; }
    }

    public double LastRetryAfterSeconds
    {
        get { lock (_lock) return _lastRetryAfterSeconds; }
    }

    public bool IsBlocked => BlockedUntil > DateTimeOffset.UtcNow;

    /// <summary>Completes the next time a longer block is recorded.</summary>
    public Task Signal => Volatile.Read(ref _signal).Task;

    /// <summary>
    /// Records a retry-after from the API. Returns true when the new block is later
    /// than the one already known, i.e. when the caller should react.
    /// </summary>
    public bool Report(double retryAfterSeconds)
    {
        if (retryAfterSeconds <= 0)
        {
            return false;
        }

        var until = DateTimeOffset.UtcNow.AddSeconds(retryAfterSeconds);
        TaskCompletionSource signal;

        lock (_lock)
        {
            _lastRetryAfterSeconds = retryAfterSeconds;
            if (until <= _blockedUntil)
            {
                return false;
            }

            _blockedUntil = until;
            signal = _signal;
            _signal = NewSignal();
        }

        signal.TrySetResult();
        return true;
    }

    /// <summary>Restores a block recorded by an earlier process.</summary>
    public bool Restore(DateTimeOffset until)
    {
        if (until <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        lock (_lock)
        {
            if (until <= _blockedUntil)
            {
                return false;
            }

            _blockedUntil = until;
            _lastRetryAfterSeconds = Math.Max(0, (until - DateTimeOffset.UtcNow).TotalSeconds);
        }

        return true;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
