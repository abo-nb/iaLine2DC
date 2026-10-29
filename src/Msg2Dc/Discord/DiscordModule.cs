using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Msg2Dc.Configuration;

namespace Msg2Dc.Discord;

public static class DiscordModule
{
    public static IServiceCollection AddDiscordModule(this IServiceCollection services)
    {
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
            ConnectionTimeout = 30000
        };

        return new DiscordSocketClient(config);
    }
}
