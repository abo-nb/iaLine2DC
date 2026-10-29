using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Msg2Dc.Configuration;

namespace Msg2Dc.Discord;

public static class DiscordModule
{
    public static IServiceCollection AddDiscordModule(this IServiceCollection services)
    {
        services.AddSingleton<DiscordRateLimitGuard>();
        services.AddSingleton<DiscordSocketClient>(provider => CreateSocketClient(provider));
        services.AddSingleton<DiscordCommandHandler>();
        services.AddSingleton<IDiscordPublisher, DiscordPublisher>();
        services.AddHostedService<DiscordBotService>();
        return services;
    }

    private static DiscordSocketClient CreateSocketClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<DiscordOptions>>().Value;

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogLevel = options.Enabled ? LogSeverity.Info : LogSeverity.Warning,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false,
            ConnectionTimeout = 30000,

            // Skip GET /gateway/bot. That route has a tiny per-token bucket, and when it
            // 429s Discord.Net retries it in a tight loop instead of honouring retry-after.
            // gateway.discord.gg is the stable host the endpoint hands out anyway.
            GatewayHost = "wss://gateway.discord.gg",

            DefaultRatelimitCallback = info =>
            {
                provider.GetRequiredService<DiscordRateLimitGuard>().Report(info);
                return Task.CompletedTask;
            }
        };

        return new DiscordSocketClient(config);
    }
}
