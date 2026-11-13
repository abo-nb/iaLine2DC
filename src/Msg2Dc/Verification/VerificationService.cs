using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using MongoDB.Bson;
using MongoDB.Driver;
using Msg2Dc.Discord;
using Msg2Dc.Storage;

namespace Msg2Dc.Verification;

public sealed record VerifiedMember(
    string Id,
    string Username,
    string? GlobalName,
    string? Nickname,
    string? AvatarUrl);

public sealed record VerifiedRole(string Id, string Name);

public sealed record VerificationUser(
    string Id,
    string Username,
    string? GlobalName,
    string? Nickname,
    string? AvatarUrl,
    string GuildId,
    IReadOnlyList<VerifiedRole> Roles,
    DateTime? JoinedAt);

public sealed record VerificationTicket(string RequestId, DateTime ExpiresAt, string Delivery);

public sealed record VerificationTokens(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Carries an HTTP status so the endpoints can answer without translating every failure.
/// </summary>
public sealed class VerificationException : Exception
{
    public VerificationException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>
/// Discord-side identity verification for other services: pick a channel, list the members
/// who can see it, DM one of them a short code, and swap that code for bearer tokens.
/// The relying party only ever starts the flow with a Discord user id it already has.
/// </summary>
public sealed class VerificationService
{
    private const ulong AdministratorBit = 1UL << 3;
    private const ulong ViewChannelBit = 1UL << 10;
    private const int MaxAttempts = 5;

    // Excludes look-alikes (0/O, 1/I) so a code read off a phone is unambiguous.
    private const string LetterAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string DigitAlphabet = "23456789";

    private const int MaxRequestsPerUser = 4;
    private static readonly TimeSpan UserRequestWindow = TimeSpan.FromMinutes(10);

    private readonly MongoStore _store;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<VerificationService> _log;

    public VerificationService(
        MongoStore store,
        DiscordSocketClient client,
        ILogger<VerificationService> log)
    {
        _store = store;
        _client = client;
        _log = log;
    }

    public async Task<VerificationSettingDoc?> GetSettingAsync(CancellationToken ct) =>
        await _store.VerificationSettings
            .Find(Builders<VerificationSettingDoc>.Filter.Empty)
            .FirstOrDefaultAsync(ct);

    public async Task<VerificationSettingDoc> SetChannelAsync(
        ulong guildId,
        ulong channelId,
        ulong userId,
        CancellationToken ct)
    {
        var existing = await GetSettingAsync(ct);
        var now = DateTime.UtcNow;

        var setting = new VerificationSettingDoc
        {
            GuildId = guildId.ToString(),
            ChannelId = channelId.ToString(),
            ApiKey = string.IsNullOrWhiteSpace(existing?.ApiKey) ? NewSecret(24) : existing!.ApiKey,
            CodeTtlMinutes = existing?.CodeTtlMinutes ?? 5,
            AccessTokenMinutes = existing?.AccessTokenMinutes ?? 60,
            RefreshTokenDays = existing?.RefreshTokenDays ?? 30,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            UpdatedBy = userId.ToString()
        };

        setting.Id = existing?.Id ?? default;

        await _store.VerificationSettings.ReplaceOneAsync(
            Builders<VerificationSettingDoc>.Filter.Empty,
            setting,
            new ReplaceOptions { IsUpsert = true },
            ct);

        return setting;
    }

    public async Task<VerificationSettingDoc?> RotateApiKeyAsync(CancellationToken ct)
    {
        var existing = await GetSettingAsync(ct);
        if (existing is null)
        {
            return null;
        }

        existing.ApiKey = NewSecret(24);
        existing.UpdatedAt = DateTime.UtcNow;

        await _store.VerificationSettings.ReplaceOneAsync(
            Builders<VerificationSettingDoc>.Filter.Eq(s => s.Id, existing.Id),
            existing,
            cancellationToken: ct);

        return existing;
    }

    public async Task<bool> IsApiKeyValidAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var setting = await GetSettingAsync(ct);
        if (setting is null || string.IsNullOrWhiteSpace(setting.ApiKey))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(setting.ApiKey);
        var actual = Encoding.UTF8.GetBytes(apiKey.Trim());

        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Everyone in the configured guild who can view the configured channel. Discord has no
    /// "members of a channel" endpoint, so the guild member list is paged and the effective
    /// permission of each member is computed against the channel overwrites.
    /// </summary>
    public async Task<(VerificationSettingDoc Setting, IReadOnlyList<VerifiedMember> Members)> ListMembersAsync(
        CancellationToken ct)
    {
        var setting = await GetSettingAsync(ct)
                      ?? throw new VerificationException(404, "尚未配置验证频道");
        var guild = await GetGuildAsync(setting, ct);
        var channel = await guild.GetChannelAsync(ParseId(setting.ChannelId, "channelId"), Options(ct))
                      ?? throw new VerificationException(404, "验证频道不存在");

        var rolePermissions = guild.Roles.ToDictionary(r => r.Id, r => r.Permissions.RawValue);
        var members = new List<VerifiedMember>();

        IReadOnlyCollection<IGuildUser> users;
        try
        {
            users = await ((IGuild)guild).GetUsersAsync(CacheMode.AllowDownload, Options(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Member listing failed for guild {GuildId}", setting.GuildId);
            throw new VerificationException(503,
                "无法枚举成员：需要在 Discord 开发者后台开启 Server Members Intent");
        }

        foreach (var user in users)
        {
            if (user.IsBot || !CanView(user, guild, channel, rolePermissions))
            {
                continue;
            }

            members.Add(new VerifiedMember(
                user.Id.ToString(),
                user.Username,
                user.GlobalName,
                user.Nickname,
                user.GetAvatarUrl()));
        }

        return (setting, members);
    }

    public async Task<VerificationTicket> CreateRequestAsync(string userId, CancellationToken ct)
    {
        var setting = await GetSettingAsync(ct)
                      ?? throw new VerificationException(404, "尚未配置验证频道");
        var uid = ParseId(userId, "userId");
        var guild = await GetGuildAsync(setting, ct);

        RestGuildUser? user;
        try
        {
            user = await guild.GetUserAsync(uid, Options(ct));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Member lookup failed for {UserId}", userId);
            user = null;
        }

        if (user is null)
        {
            throw new VerificationException(404, "该用户不在服务器中");
        }

        // People retry when the code does not arrive, so allow a few attempts per window
        // instead of a hard interval between requests.
        var cutoff = DateTime.UtcNow.Subtract(UserRequestWindow);
        var recent = await _store.VerificationCodes.CountDocumentsAsync(
            Builders<VerificationCodeDoc>.Filter.And(
                Builders<VerificationCodeDoc>.Filter.Eq(c => c.UserId, userId),
                Builders<VerificationCodeDoc>.Filter.Gte(c => c.CreatedAt, cutoff)),
            cancellationToken: ct);

        if (recent >= MaxRequestsPerUser)
        {
            throw new VerificationException(429,
                $"验证码请求太频繁：{UserRequestWindow.TotalMinutes:F0} 分钟内最多 {MaxRequestsPerUser} 次");
        }

        // A new code supersedes the old one: drop its thread and mark it used.
        await SupersedePreviousCodesAsync(userId, ct);

        var code = NewCode();
        var requestId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromMinutes(Math.Clamp(setting.CodeTtlMinutes, 1, 30));

        await _store.VerificationCodes.InsertOneAsync(new VerificationCodeDoc
        {
            RequestId = requestId,
            GuildId = setting.GuildId,
            ChannelId = setting.ChannelId,
            UserId = userId,
            CodeHash = HashCode(requestId, code),
            CreatedAt = now,
            ExpiresAt = now.Add(ttl)
        }, cancellationToken: ct);

        var text = $"身份验证码：{code}（{ttl.TotalMinutes:F0} 分钟内有效）";

        string delivery;
        try
        {
            var dm = await user.CreateDMChannelAsync(Options(ct));
            await dm.SendMessageAsync(text);
            delivery = "dm";
        }
        catch (Exception ex)
        {
            // Most often the member turned off "direct messages from server members".
            // Fall back to a private thread in the configured channel, which only that
            // member (and the bot) can read.
            _log.LogInformation("DM to {UserId} failed ({Reason}); using a private thread", userId, ex.Message);

            try
            {
                var threadId = await SendCodeInThreadAsync(setting, guild, user, code, ttl, ct);
                await _store.VerificationCodes.UpdateOneAsync(
                    Builders<VerificationCodeDoc>.Filter.Eq(c => c.RequestId, requestId),
                    Builders<VerificationCodeDoc>.Update.Set(c => c.ThreadId, threadId),
                    cancellationToken: ct);
                delivery = "thread";
            }
            catch (Exception threadEx)
            {
                _log.LogError(threadEx, "Could not deliver verification code to {UserId}", userId);
                await _store.VerificationCodes.DeleteOneAsync(
                    Builders<VerificationCodeDoc>.Filter.Eq(c => c.RequestId, requestId), ct);
                throw new VerificationException(502, "无法投递验证码：私信与私密线程都失败了");
            }
        }

        return new VerificationTicket(requestId, now.Add(ttl), delivery);
    }

    public async Task<VerificationTokens> ConfirmAsync(string requestId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(code))
        {
            throw new VerificationException(400, "requestId 与验证码不能为空");
        }

        var setting = await GetSettingAsync(ct)
                      ?? throw new VerificationException(404, "尚未配置验证频道");

        var request = await _store.VerificationCodes
            .Find(Builders<VerificationCodeDoc>.Filter.Eq(c => c.RequestId, requestId))
            .FirstOrDefaultAsync(ct);

        if (request is null || request.ConsumedAt is not null)
        {
            throw new VerificationException(400, "验证请求不存在或已使用");
        }

        if (request.ExpiresAt < DateTime.UtcNow)
        {
            throw new VerificationException(400, "验证码已过期，请重新发起");
        }

        if (request.Attempts >= MaxAttempts)
        {
            throw new VerificationException(400, "尝试次数过多，请重新发起");
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (!FixedEquals(HashCode(requestId, normalized), request.CodeHash))
        {
            await _store.VerificationCodes.UpdateOneAsync(
                Builders<VerificationCodeDoc>.Filter.Eq(c => c.Id, request.Id),
                Builders<VerificationCodeDoc>.Update.Inc(c => c.Attempts, 1),
                cancellationToken: ct);

            throw new VerificationException(400, "验证码不正确");
        }

        await _store.VerificationCodes.UpdateOneAsync(
            Builders<VerificationCodeDoc>.Filter.Eq(c => c.Id, request.Id),
            Builders<VerificationCodeDoc>.Update.Set(c => c.ConsumedAt, DateTime.UtcNow),
            cancellationToken: ct);

        var tokens = await IssueTokensAsync(request.UserId, request.GuildId, setting, ct);

        if (ulong.TryParse(request.ThreadId, out var threadId) && await TryDeleteThreadAsync(threadId, ct))
        {
            await ClearThreadAsync(request.Id, ct);
        }

        return tokens;
    }

    /// <summary>
    /// Deletes the private threads of codes that expired without being confirmed, and drops
    /// old request/token rows. Runs on a timer and before a member asks for a new code.
    /// </summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var stale = await _store.VerificationCodes
            .Find(Builders<VerificationCodeDoc>.Filter.And(
                Builders<VerificationCodeDoc>.Filter.Lt(c => c.ExpiresAt, now),
                Builders<VerificationCodeDoc>.Filter.Ne(c => c.ThreadId, null)))
            .Limit(50)
            .ToListAsync(ct);

        if (stale.Count > 0)
        {
            _log.LogInformation("Found {Count} expired verification code(s) to clean up", stale.Count);
        }

        var deleted = 0;
        foreach (var code in stale)
        {
            if (ulong.TryParse(code.ThreadId, out var threadId) && await TryDeleteThreadAsync(threadId, ct))
            {
                await ClearThreadAsync(code.Id, ct);
                deleted++;
            }
        }

        await _store.VerificationCodes.DeleteManyAsync(
            Builders<VerificationCodeDoc>.Filter.Lt(c => c.CreatedAt, now.AddDays(-1)),
            ct);

        await _store.VerificationTokens.DeleteManyAsync(
            Builders<VerificationTokenDoc>.Filter.Lt(t => t.RefreshExpiresAt, now.AddDays(-7)),
            ct);

        return deleted;
    }

    /// <summary>
    /// A retried code replaces the previous one: its thread goes away and the old code can
    /// no longer be confirmed, so only the newest one is live.
    /// </summary>
    private async Task SupersedePreviousCodesAsync(string userId, CancellationToken ct)
    {
        try
        {
            var active = await _store.VerificationCodes
                .Find(Builders<VerificationCodeDoc>.Filter.And(
                    Builders<VerificationCodeDoc>.Filter.Eq(c => c.UserId, userId),
                    Builders<VerificationCodeDoc>.Filter.Eq(c => c.ConsumedAt, null)))
                .ToListAsync(ct);

            if (active.Count == 0)
            {
                return;
            }

            foreach (var code in active)
            {
                if (ulong.TryParse(code.ThreadId, out var threadId) && await TryDeleteThreadAsync(threadId, ct))
                {
                    await ClearThreadAsync(code.Id, ct);
                }
            }

            await _store.VerificationCodes.UpdateManyAsync(
                Builders<VerificationCodeDoc>.Filter.In(c => c.Id, active.Select(c => c.Id)),
                Builders<VerificationCodeDoc>.Update.Set(c => c.ConsumedAt, DateTime.UtcNow),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not supersede previous verification codes for {UserId}", userId);
        }
    }

    private Task ClearThreadAsync(ObjectId codeId, CancellationToken ct) =>
        _store.VerificationCodes.UpdateOneAsync(
            Builders<VerificationCodeDoc>.Filter.Eq(c => c.Id, codeId),
            Builders<VerificationCodeDoc>.Update.Set(c => c.ThreadId, (string?)null),
            cancellationToken: ct);

    private async Task<bool> TryDeleteThreadAsync(ulong threadId, CancellationToken ct)
    {
        try
        {
            if (await _client.Rest.GetChannelAsync(threadId, Options(ct)) is IDeletable deletable)
            {
                await deletable.DeleteAsync();
            }

            return true;
        }
        catch (global::Discord.Net.HttpException ex) when ((int)ex.HttpCode == 404)
        {
            // Already gone; nothing left to delete.
            return true;
        }
        catch (global::Discord.Net.HttpException ex) when ((int)ex.HttpCode == 403)
        {
            _log.LogWarning(
                "Cannot delete verification thread {ThreadId}: give the bot Manage Threads in the verification channel",
                threadId);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not delete verification thread {ThreadId}", threadId);
            return false;
        }
    }

    /// <summary>
    /// Falls back to a private thread in the configured channel when the member cannot be
    /// DM'd; only the member and the bot can read it, and it is deleted after confirmation.
    /// </summary>
    private async Task<string> SendCodeInThreadAsync(
        VerificationSettingDoc setting,
        RestGuild guild,
        RestGuildUser user,
        string code,
        TimeSpan ttl,
        CancellationToken ct)
    {
        if (await guild.GetChannelAsync(ParseId(setting.ChannelId, "channelId"), Options(ct)) is not ITextChannel channel)
        {
            throw new VerificationException(502, "验证频道不是文字频道，无法建立私密线程");
        }

        // The code also goes in the thread name: a private thread is only listed for the
        // invited member (and staff with Manage Threads), so it is the quickest place to
        // read it, e.g. "[AB12]-小明".
        var title = DiscordText.Clip($"[{code}]-{user.Username}", 100);

        var thread = await channel.CreateThreadAsync(
            title,
            ThreadType.PrivateThread,
            autoArchiveDuration: ThreadArchiveDuration.OneHour,
            invitable: false,
            options: Options(ct));

        await thread.AddUserAsync(user, Options(ct));
        await thread.SendMessageAsync(
            $"身份验证码：{code}（{ttl.TotalMinutes:F0} 分钟内有效）",
            allowedMentions: AllowedMentions.None);

        return thread.Id.ToString();
    }

    public async Task<VerificationTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new VerificationException(400, "refreshToken 不能为空");
        }

        var setting = await GetSettingAsync(ct)
                      ?? throw new VerificationException(404, "尚未配置验证频道");

        var token = await _store.VerificationTokens
            .Find(Builders<VerificationTokenDoc>.Filter.Eq(t => t.RefreshTokenHash, HashSecret(refreshToken)))
            .FirstOrDefaultAsync(ct);

        if (token is null || token.RevokedAt is not null)
        {
            throw new VerificationException(401, "refreshToken 无效");
        }

        if (token.RefreshExpiresAt < DateTime.UtcNow)
        {
            throw new VerificationException(401, "refreshToken 已过期，请重新验证");
        }

        await _store.VerificationTokens.UpdateOneAsync(
            Builders<VerificationTokenDoc>.Filter.Eq(t => t.Id, token.Id),
            Builders<VerificationTokenDoc>.Update.Set(t => t.RevokedAt, DateTime.UtcNow),
            cancellationToken: ct);

        return await IssueTokensAsync(token.UserId, token.GuildId, setting, ct);
    }

    public async Task<VerificationUser> GetUserAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new VerificationException(401, "缺少 accessToken");
        }

        var token = await _store.VerificationTokens
            .Find(Builders<VerificationTokenDoc>.Filter.Eq(t => t.AccessTokenHash, HashSecret(accessToken)))
            .FirstOrDefaultAsync(ct);

        if (token is null || token.RevokedAt is not null)
        {
            throw new VerificationException(401, "accessToken 无效");
        }

        if (token.AccessExpiresAt < DateTime.UtcNow)
        {
            throw new VerificationException(401, "accessToken 已过期");
        }

        var setting = new VerificationSettingDoc { GuildId = token.GuildId };
        var guild = await GetGuildAsync(setting, ct);
        var user = await guild.GetUserAsync(ParseId(token.UserId, "userId"), Options(ct));

        if (user is null)
        {
            throw new VerificationException(404, "该用户已不在服务器");
        }

        var roles = user.RoleIds
            .Select(id => guild.Roles.FirstOrDefault(r => r.Id == id))
            .Where(r => r is not null)
            .Select(r => new VerifiedRole(r!.Id.ToString(), r.Name))
            .ToList();

        return new VerificationUser(
            user.Id.ToString(),
            user.Username,
            user.GlobalName,
            user.Nickname,
            user.GetAvatarUrl(),
            token.GuildId,
            roles,
            user.JoinedAt?.UtcDateTime);
    }

    private async Task<VerificationTokens> IssueTokensAsync(
        string userId,
        string guildId,
        VerificationSettingDoc setting,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var accessToken = NewSecret(32);
        var refreshToken = NewSecret(32);
        var accessMinutes = Math.Clamp(setting.AccessTokenMinutes, 5, 1440);
        var refreshDays = Math.Clamp(setting.RefreshTokenDays, 1, 365);

        await _store.VerificationTokens.InsertOneAsync(new VerificationTokenDoc
        {
            UserId = userId,
            GuildId = guildId,
            AccessTokenHash = HashSecret(accessToken),
            RefreshTokenHash = HashSecret(refreshToken),
            AccessExpiresAt = now.AddMinutes(accessMinutes),
            RefreshExpiresAt = now.AddDays(refreshDays),
            CreatedAt = now
        }, cancellationToken: ct);

        return new VerificationTokens(accessToken, refreshToken, accessMinutes * 60);
    }

    private async Task<RestGuild> GetGuildAsync(VerificationSettingDoc setting, CancellationToken ct)
    {
        if (!ulong.TryParse(setting.GuildId, out var guildId) || guildId == 0)
        {
            throw new VerificationException(500, "验证服务器配置无效");
        }

        try
        {
            return await _client.Rest.GetGuildAsync(guildId, Options(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load guild {GuildId} for verification", guildId);
            throw new VerificationException(502, "无法读取服务器信息");
        }
    }

    private static bool CanView(
        IGuildUser user,
        RestGuild guild,
        RestGuildChannel channel,
        IReadOnlyDictionary<ulong, ulong> rolePermissions)
    {
        var permissions = guild.EveryoneRole.Permissions.RawValue;
        foreach (var roleId in user.RoleIds)
        {
            if (rolePermissions.TryGetValue(roleId, out var value))
            {
                permissions |= value;
            }
        }

        if ((permissions & AdministratorBit) != 0)
        {
            return true;
        }

        var roleIds = user.RoleIds.ToHashSet();
        ulong allow = 0;
        ulong deny = 0;

        foreach (var overwrite in channel.PermissionOverwrites)
        {
            if (overwrite.TargetType != PermissionTarget.Role)
            {
                continue;
            }

            if (overwrite.TargetId == guild.Id || roleIds.Contains(overwrite.TargetId))
            {
                allow |= overwrite.Permissions.AllowValue;
                deny |= overwrite.Permissions.DenyValue;
            }
        }

        permissions = (permissions & ~deny) | allow;

        foreach (var overwrite in channel.PermissionOverwrites)
        {
            if (overwrite.TargetType == PermissionTarget.User && overwrite.TargetId == user.Id)
            {
                permissions = (permissions & ~overwrite.Permissions.DenyValue) | overwrite.Permissions.AllowValue;
            }
        }

        return (permissions & ViewChannelBit) != 0;
    }

    private static ulong ParseId(string? value, string name)
    {
        if (!ulong.TryParse(value, out var id) || id == 0)
        {
            throw new VerificationException(400, $"{name} 不是合法的 Discord ID");
        }

        return id;
    }

    private static string NewCode()
    {
        // Two letters and two digits in random order: always mixed, always uppercase.
        var chars = new[]
        {
            LetterAlphabet[RandomNumberGenerator.GetInt32(LetterAlphabet.Length)],
            LetterAlphabet[RandomNumberGenerator.GetInt32(LetterAlphabet.Length)],
            DigitAlphabet[RandomNumberGenerator.GetInt32(DigitAlphabet.Length)],
            DigitAlphabet[RandomNumberGenerator.GetInt32(DigitAlphabet.Length)]
        };

        RandomNumberGenerator.Shuffle(chars);
        return new string(chars);
    }

    private static string NewSecret(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string HashCode(string requestId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId}:{code}")));

    private static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static RequestOptions Options(CancellationToken ct) =>
        new() { RetryMode = RetryMode.Retry502, Timeout = 15000, CancelToken = ct };
}
