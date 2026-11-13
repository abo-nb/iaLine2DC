using Microsoft.Extensions.Logging.Abstractions;
using Msg2Dc.Verification;
using Xunit;

namespace Msg2Dc.Tests;

public sealed class AbuseGuardTests
{
    private static AbuseGuard NewGuard() => new(NullLogger<AbuseGuard>.Instance);

    [Fact]
    public void StaysBelowTheLimit()
    {
        var guard = NewGuard();

        for (var i = 0; i < 300; i++)
        {
            var (banned, _) = guard.Check("203.0.113.5");
            Assert.False(banned);
        }
    }

    [Fact]
    public void BansAfterTheLimitWithinAMinute()
    {
        var guard = NewGuard();

        for (var i = 0; i < 300; i++)
        {
            guard.Check("203.0.113.5");
        }

        var (banned, retryAfter) = guard.Check("203.0.113.5");

        Assert.True(banned);
        Assert.InRange(retryAfter, 590, 600);
    }

    [Fact]
    public void OtherAddressesAreNotAffected()
    {
        var guard = NewGuard();
        for (var i = 0; i < 305; i++)
        {
            guard.Check("203.0.113.5");
        }

        Assert.True(guard.Check("203.0.113.5").Banned);
        Assert.False(guard.Check("203.0.113.6").Banned);
    }

    [Fact]
    public void MissingAddressIsIgnored()
    {
        var guard = NewGuard();

        Assert.False(guard.Check(null).Banned);
        Assert.False(guard.Check("").Banned);
    }
}
