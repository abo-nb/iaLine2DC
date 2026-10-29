using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Msg2Dc.Configuration;

namespace Msg2Dc.Discord;

public sealed class DiscordBotService : BackgroundService
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StormWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealthySession = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OfflineLimit = TimeSpan.FromMinutes(3);
    private const int StormDisconnects = 3;

    private readonly DiscordSocketClient _client;
    private readonly DiscordCommandHandler _commands;
    private readonly DiscordRateLimitGuard _guard;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordBotService> _log;

    private readonly List<DateTime> _disconnectTimes = new();

    private ulong? _guildId;
    private DateTime _readySince = DateTime.MinValue;
    private DateTime _lastDisconnectedAt = DateTime.MinValue;
    private int _stormCount;
    private int _registered;
    private bool _wired;
    private volatile bool _cooling;

    public DiscordBotService(
        DiscordSocketClient client,
        DiscordCommandHandler commands,
        DiscordRateLimitGuard guard,
        IOptions<DiscordOptions> options,
        ILogger<DiscordBotService> log)
    {
        _client = client;
        _commands = commands;
        _guard = guard;
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

        await _guard.LoadAsync(stoppingToken);

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
        var now = DateTime.UtcNow;

        if (_readySince != DateTime.MinValue && now - _readySince >= HealthySession)
        {
            lock (_disconnectTimes)
            {
                _disconnectTimes.Clear();
            }

            _stormCount = 0;
        }

        lock (_disconnectTimes)
        {
            _disconnectTimes.Add(now);
            if (_disconnectTimes.Count > 30)
            {
                _disconnectTimes.RemoveAt(0);
            }
        }

        _lastDisconnectedAt = now;
        _log.LogWarning(exception, "Discord gateway disconnected");
        return Task.CompletedTask;
    }

    private Task OnReadyAsync()
    {
        _readySince = DateTime.UtcNow;
        _lastDisconnectedAt = DateTime.MinValue;

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

    private async Task<bool> WaitWhileRateLimitedAsync(CancellationToken ct)
    {
        while (_guard.IsBlocked)
        {
            var wait = _guard.BlockedUntil - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            if (wait < TimeSpan.FromSeconds(1))
            {
                wait = TimeSpan.FromSeconds(1);
            }

            _log.LogWarning(
                "Discord is rate limited; waiting {Minutes:F1} minute(s) before connecting",
                wait.TotalMinutes);

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return !ct.IsCancellationRequested;
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var baseCooldown = TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wake up as soon as Discord tells us to back off, otherwise re-check every 15s.
                await Task.WhenAny(Task.Delay(HealthCheckInterval, ct), _guard.Signal);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_cooling)
            {
                continue;
            }

            PruneDisconnects();

            TimeSpan wait;
            string reason;

            if (_guard.IsBlocked && _client.ConnectionState != ConnectionState.Connected)
            {
                wait = _guard.BlockedUntil - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                reason = $"Discord rate limit ({_guard.LastRetryAfterSeconds:F0}s retry-after)";
            }
            else if (DisconnectsInWindow() >= StormDisconnects || HasBeenOfflineTooLong())
            {
                _stormCount++;
                var minutes = Math.Min(120, baseCooldown.TotalMinutes * Math.Pow(2, _stormCount - 1));
                wait = TimeSpan.FromMinutes(minutes);
                reason = $"reconnect storm ({DisconnectsInWindow()} disconnects in 10 minutes)";
            }
            else
            {
                continue;
            }

            if (wait < TimeSpan.FromSeconds(1))
            {
                wait = TimeSpan.FromSeconds(1);
            }

            _cooling = true;
            try
            {
                _log.LogError(
                    "Stopping the Discord client for {Minutes:F0} minute(s) so the rate-limit bucket can reset: {Reason}",
                    wait.TotalMinutes, reason);

                try
                {
                    await _client.StopAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Discord stop during cooldown failed");
                }

                await Task.Delay(wait, ct);

                lock (_disconnectTimes)
                {
                    _disconnectTimes.Clear();
                }

                _readySince = DateTime.MinValue;

                if (_guard.IsBlocked)
                {
                    _log.LogWarning(
                        "Discord rate limit was extended; staying offline until {Until:u}",
                        _guard.BlockedUntil);

                    if (!await WaitWhileRateLimitedAsync(ct))
                    {
                        return;
                    }
                }

                await _client.StartAsync();
                _log.LogInformation("Discord client restarted after {Minutes:F0} minute(s)", wait.TotalMinutes);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Discord restart after cooldown failed");
            }
            finally
            {
                _cooling = false;
            }
        }
    }

    private void PruneDisconnects()
    {
        var cutoff = DateTime.UtcNow - StormWindow;
        lock (_disconnectTimes)
        {
            _disconnectTimes.RemoveAll(t => t < cutoff);
        }
    }

    private int DisconnectsInWindow()
    {
        var cutoff = DateTime.UtcNow - StormWindow;
        lock (_disconnectTimes)
        {
            return _disconnectTimes.Count(t => t >= cutoff);
        }
    }

    private bool HasBeenOfflineTooLong() =>
        !_guard.IsBlocked
        && _client.ConnectionState == ConnectionState.Disconnected
        && _lastDisconnectedAt != DateTime.MinValue
        && DateTime.UtcNow - _lastDisconnectedAt > OfflineLimit;

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

    /// <summary>
    /// Registers the slash commands once per process. Re-registering on every Ready
    /// multiplied the request volume whenever the gateway reconnected.
    /// </summary>
    private async Task RegisterCommandsAsync()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        var complete = false;

        try
        {
            if (_guard.IsBlocked)
            {
                _log.LogWarning(
                    "Slash command registration skipped until {Until:u} (Discord rate limit)",
                    _guard.BlockedUntil);
                return;
            }

            var definitions = DiscordCommands.Build();

            using var batchCancel = new CancellationTokenSource();
            _ = CancelWhenRateLimitedAsync(batchCancel);

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

                var registered = 0;
                foreach (var (name, properties) in definitions)
                {
                    if (!await TryRegisterAsync(name, () => guild.CreateApplicationCommandAsync(properties, WriteOptions(batchCancel.Token))))
                    {
                        return;
                    }

                    registered++;
                }

                _log.LogInformation("Registered {Count} guild slash commands in guild {GuildId}",
                    registered, guildId);
            }
            else
            {
                var registered = 0;
                foreach (var (name, properties) in definitions)
                {
                    if (!await TryRegisterAsync(name, () => _client.CreateGlobalApplicationCommandAsync(properties, WriteOptions(batchCancel.Token))))
                    {
                        return;
                    }

                    registered++;
                }

                _log.LogInformation(
                    "Registered {Count} global slash commands (Discord may take up to an hour to show them)",
                    registered);
            }

            complete = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Slash command registration failed");
        }
        finally
        {
            if (!complete)
            {
                // Retried by the next Ready, which only happens after the rate limit cleared.
                Interlocked.Exchange(ref _registered, 0);
            }
        }
    }

    /// <summary>
    /// Discord.Net keeps resending a 429'd request in a tight loop when the response
    /// carries no parsable reset time, so cancel our own batch as soon as the guard
    /// sees a rate limit. The in-flight request then aborts on its next iteration.
    /// </summary>
    private async Task CancelWhenRateLimitedAsync(CancellationTokenSource batchCancel)
    {
        try
        {
            var signal = _guard.Signal;
            await Task.WhenAny(signal, Task.Delay(TimeSpan.FromMinutes(1)));
            if (signal.IsCompleted)
            {
                batchCancel.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // The batch finished first.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stopping the command-registration batch failed");
        }
    }

    private async Task<bool> TryRegisterAsync(string name, Func<Task> register)
    {
        try
        {
            await register();
            return true;
        }
        catch (RateLimitedException)
        {
            _log.LogWarning(
                "Slash command /{Command} hit a Discord rate limit; aborting this batch until the bucket resets",
                name);
            return false;
        }
        catch (Exception ex) when (_guard.IsBlocked)
        {
            _log.LogWarning(
                "Stopped registering slash commands at /{Command}: Discord rate limit is active ({Message})",
                name, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to register slash command /{Command}", name);

            // A timeout or any other failure right after a 429 means the bucket is still
            // hot; stop the batch instead of letting every command hammer it in turn.
            return !_guard.IsBlocked;
        }
    }

    /// <summary>
    /// Never let Discord.Net retry a rate-limited request: with the default
    /// RetryMode.AlwaysRetry it resends in a tight loop and escalates the ban.
    /// The timeout is the only thing that stops the loop when a 429 carries no
    /// parsable reset time, so keep it short.
    /// </summary>
    private static RequestOptions WriteOptions(CancellationToken ct) =>
        new() { RetryMode = RetryMode.Retry502, Timeout = 15000, CancelToken = ct };

    private static string BuildInviteUrl(ulong clientId, ulong? guildId)
    {
        const long permissions = 1024 | 2048 | 8192 | 16384 | 32768 | 65536;
        var url = $"https://discord.com/oauth2/authorize?client_id={clientId}&permissions={permissions}&scope=bot%20applications.commands";
        return guildId is null ? url : $"{url}&guild_id={guildId}&disable_guild_select=true";
    }

    private static ulong? ParseGuildId(string value) =>
        ulong.TryParse(value?.Trim(), out var id) && id != 0 ? id : null;
}
