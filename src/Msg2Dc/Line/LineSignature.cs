using System.Security.Cryptography;
using System.Text;

namespace Msg2Dc.Line;

public static class LineSignature
{
    public static bool Verify(string channelSecret, string body, string? signature)
    {
        if (string.IsNullOrEmpty(channelSecret) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        var expected = Encoding.UTF8.GetBytes(computed);
        var actual = Encoding.UTF8.GetBytes(signature);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string Sign(string channelSecret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }
}
