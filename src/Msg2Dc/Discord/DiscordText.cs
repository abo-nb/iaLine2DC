namespace Msg2Dc.Discord;

internal static class DiscordText
{
    public const int MessageMax = 2000;

    public static string Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0)
        {
            return "";
        }

        var text = value.Trim();
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }

    public static string Relative(DateTime value) => $"<t:{ToUnix(value)}:R>";

    public static string Absolute(DateTime value) => $"<t:{ToUnix(value)}:f>";

    public static string JumpLink(ulong guildId, ulong channelId, ulong messageId) =>
        $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    private static long ToUnix(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }
}
