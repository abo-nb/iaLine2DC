using Msg2Dc.Discord;
using Xunit;

namespace Msg2Dc.Tests;

public sealed class RateLimitStateTests
{
    [Fact]
    public void ReportBlocksForRetryAfter()
    {
        var state = new RateLimitState();
        var before = DateTimeOffset.UtcNow;

        Assert.True(state.Report(30));

        Assert.True(state.IsBlocked);
        Assert.InRange(state.BlockedUntil, before.AddSeconds(29), DateTimeOffset.UtcNow.AddSeconds(31));
        Assert.Equal(30, state.LastRetryAfterSeconds);
    }

    [Fact]
    public void ShorterRetryAfterDoesNotShortenTheBlock()
    {
        var state = new RateLimitState();
        Assert.True(state.Report(60));

        var blockedUntil = state.BlockedUntil;

        Assert.False(state.Report(5));
        Assert.Equal(blockedUntil, state.BlockedUntil);
    }

    [Fact]
    public void LongerRetryAfterExtendsTheBlock()
    {
        var state = new RateLimitState();
        Assert.True(state.Report(30));

        var first = state.BlockedUntil;
        Assert.True(state.Report(120));

        Assert.True(state.BlockedUntil > first);
        Assert.Equal(120, state.LastRetryAfterSeconds);
    }

    [Fact]
    public async Task ReportWakesTheSupervisor()
    {
        var state = new RateLimitState();
        var signal = state.Signal;
        Assert.False(signal.IsCompleted);

        state.Report(10);

        await signal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(state.Signal.IsCompleted);
    }

    [Fact]
    public void RestoreIgnoresExpiredState()
    {
        var state = new RateLimitState();

        Assert.False(state.Restore(DateTimeOffset.UtcNow.AddSeconds(-5)));
        Assert.False(state.IsBlocked);

        Assert.True(state.Restore(DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.True(state.IsBlocked);
    }

    [Fact]
    public void InvalidRetryAfterIsIgnored()
    {
        var state = new RateLimitState();

        Assert.False(state.Report(0));
        Assert.False(state.Report(-1));
        Assert.False(state.IsBlocked);
    }
}
