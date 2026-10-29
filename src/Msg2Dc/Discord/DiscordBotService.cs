using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Msg2Dc.Configuration;

namespace Msg2Dc.Discord;

public sealed class DiscordBotService : BackgroundService
{
    private static readonly TimeSpan RegisterInterval = TimeSpan.FromSeconds(30);

    private readonly DiscordSocketClient _client;
    private readonly DiscordCommandHandler _commands;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordBotService> _log;
    private readonly SemaphoreSlim _registerLock = new(1, 1);

    private ulong? _guildId;
    private DateTime _registeredAt = DateTime.MinValue;
    private bool _wired;

    private int _disconnectsSinceReady;
    private int _gatewayRateLimitHits;
    private int _stormCount;
    private DateTime _lastDisconnectedAt = DateTime.MinValue;
    private volatile bool _cooling;

    public DiscordBotService(
        DiscordSocketClient client,
        DiscordCommandHandler commands,
        IOptions<DiscordOptions> options,
        ILogger<DiscordBotService> log)
    {
        _client = client;
        _commands = commands;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Discord is disabled (Discord:Enabled=false); the bot will not connect");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _log.LogWarning("Discord token is empty; the bot will not connect. Set Discord:Token (or Discord__Token)");
            return;
        }

        _guildId = ParseGuildId(_options.GuildId);
        if (_guildId is null && !string.IsNullOrWhiteSpace(_options.GuildId))
        {
            _log.LogWarning(
                "Discord GuildId '{GuildId}' is not a valid snowflake; slash commands will be registered globally",
                _options.GuildId);
        }

        Wire();

        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                if (_client.LoginState != LoginState.LoggedIn)
                {
                    await _client.LoginAsync(TokenType.Bot, _options.Token);
                }

                if (_client.CurrentUser is { } self)
                {
                    _log.LogInformation(
                        "Discord invite link (open as a server admin to add the bot): {InviteUrl}",
                        BuildInviteUrl(self.Id, _guildId));
                }

                await _client.StartAsync();
                _log.LogInformation("Discord gateway started");
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Unwire();
                return;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, attempt * 5));
                _log.LogError(ex, "Discord login failed (attempt {Attempt}); retrying in {Seconds}s",
                    attempt, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    Unwire();
                    return;
                }
            }
        }

        await SuperviseAsync(stoppingToken);

        Unwire();

        try
        {
            await _client.StopAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord client stop failed");
        }

        try
        {
            await _client.LogoutAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discord client logout failed");
        }
    }

    private void Wire()
    {
        if (_wired)
        {
            return;
        }

        _client.Log += OnLogAsync;
        _client.Connected += OnConnectedAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _wired = true;
    }

    private void Unwire()
    {
        if (!_wired)
        {
            return;
        }

        _client.Log -= OnLogAsync;
        _client.Connected -= OnConnectedAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.Ready -= OnReadyAsync;
        _client.SlashCommandExecuted -= OnSlashCommandAsync;
        _wired = false;
    }

    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug
        };

        if (message.Source == "Rest" &&
            message.Message?.Contains("gateway/bot", StringComparison.OrdinalIgnoreCase) == true)
        {
            Interlocked.Increment(ref _gatewayRateLimitHits);
        }

        _log.Log(level, message.Exception, "[Discord/{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        _lastDisconnectedAt = DateTime.MinValue;
        _log.LogInformation("Discord gateway connected");
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        if (!_cooling)
        {
            Interlocked.Increment(ref _disconnectsSinceReady);
        }

        _lastDisconnectedAt = DateTime.UtcNow;
        _log.LogWarning(exception, "Discord gateway disconnected");
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        Interlocked.Exchange(ref _disconnectsSinceReady, 0);
        Interlocked.Exchange(ref _gatewayRateLimitHits, 0);
        _lastDisconnectedAt = DateTime.MinValue;
        _stormCount = 0;

        _log.LogInformation("Discord ready as {User} in {Guilds} guild(s)",
            _client.CurrentUser?.Username ?? "unknown", _client.Guilds.Count);

        if (_client.CurrentUser is { } self)
        {
            _log.LogInformation(
                "Discord invite link (open as a server admin to add the bot): {InviteUrl}",
                BuildInviteUrl(self.Id, _guildId ?? (_client.Guilds.Count == 1 ? _client.Guilds.First().Id : null)));
        }

        _ = Task.Run(RegisterCommandsAsync);
        return Task.CompletedTask;
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var baseCooldown = TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_cooling)
            {
                continue;
            }

            var disconnects = Volatile.Read(ref _disconnectsSinceReady);
            var rateLimitHits = Volatile.Read(ref _gatewayRateLimitHits);
            var offlineTooLong = _client.ConnectionState == ConnectionState.Disconnected
                                 && _lastDisconnectedAt != DateTime.MinValue
                                 && DateTime.UtcNow - _lastDisconnectedAt > TimeSpan.FromMinutes(3);

            if (disconnects < 3 && rateLimitHits < 2 && !offlineTooLong)
            {
                continue;
            }

            _cooling = true;
            _stormCount++;
            var minutes = Math.Min(120, baseCooldown.TotalMinutes * Math.Pow(2, _stormCount - 1));
            var wait = TimeSpan.FromMinutes(minutes);

            _log.LogError(
                "Discord reconnect storm ({Disconnects} disconnects, {RateLimitHits} gateway/bot rate-limit hits); " +
                "stopping the client for {Minutes} minute(s) so the gateway rate-limit bucket can reset",
                disconnects, rateLimitHits, (int)minutes);

            try
            {
                await _client.StopAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Discord stop during cooldown failed");
            }

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Exchange(ref _disconnectsSinceReady, 0);
            Interlocked.Exchange(ref _gatewayRateLimitHits, 0);

            try
            {
                await _client.StartAsync();
                _log.LogInformation("Discord client restarted after {Minutes} minute cooldown", (int)minutes);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Discord restart after cooldown failed");
            }

            _cooling = false;
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await _commands.HandleAsync(command, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Slash command /{Command} timed out", command.CommandName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Slash command /{Command} failed", command.CommandName);
        }
    }

    private async Task RegisterCommandsAsync()
    {
        if (_registerLock.CurrentCount == 0)
        {
            return;
        }

        await _registerLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _registeredAt < RegisterInterval)
            {
                return;
            }

            var definitions = DiscordCommands.Build();

            var targetGuildId = _guildId;
            if (targetGuildId is null && _client.Guilds.Count == 1)
            {
                targetGuildId = _client.Guilds.First().Id;
                _log.LogInformation(
                    "Discord:GuildId is empty; using the only joined guild {GuildId} for command registration",
                    targetGuildId);
            }

            if (targetGuildId is ulong guildId)
            {
                var guild = _client.GetGuild(guildId);
                if (guild is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    guild = _client.GetGuild(guildId);
                }

                if (guild is null)
                {
                    _log.LogWarning(
                        "Guild {GuildId} is not available; slash commands were not registered. Invite the bot first: {InviteUrl}",
                        guildId, BuildInviteUrl(_client.CurrentUser?.Id ?? 0, guildId));
                    return;
                }

                foreach (var (name, properties) in definitions)
                {
                    await TryRegisterAsync(name, () => guild.CreateApplicationCommandAsync(properties));
                }

                _registeredAt = DateTime.UtcNow;
                _log.LogInformation("Registered {Count} guild slash commands in guild {GuildId}",
                    definitions.Count, guildId);
            }
            else
            {
                foreach (var (name, properties) in definitions)
                {
                    await TryRegisterAsync(name, () => _client.CreateGlobalApplicationCommandAsync(properties));
                }

                _registeredAt = DateTime.UtcNow;
                _log.LogInformation(
                    "Registered {Count} global slash commands (Discord may take up to an hour to show them)",
                    definitions.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Slash command registration failed");
        }
        finally
        {
            _registerLock.Release();
        }
    }

    private async Task TryRegisterAsync(string name, Func<Task> register)
    {
        try
        {
            await register();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to register slash command /{Command}", name);
        }
    }

    private static string BuildInviteUrl(ulong clientId, ulong? guildId)
    {
        const long permissions = 1024 | 2048 | 8192 | 16384 | 32768 | 65536;
        var url = $"https://discord.com/oauth2/authorize?client_id={clientId}&permissions={permissions}&scope=bot%20applications.commands";
        return guildId is null ? url : $"{url}&guild_id={guildId}&disable_guild_select=true";
    }

    private static ulong? ParseGuildId(string value) =>
        ulong.TryParse(value?.Trim(), out var id) && id != 0 ? id : null;
}
