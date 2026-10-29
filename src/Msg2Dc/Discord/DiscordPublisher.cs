using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Storage;

namespace Msg2Dc.Discord;

public sealed class DiscordPublisher : IDiscordPublisher
{
    private readonly DiscordSocketClient _client;
    private readonly MongoStore _store;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordPublisher> _log;

    public DiscordPublisher(
        DiscordSocketClient client,
        MongoStore store,
        IOptions<DiscordOptions> options,
        ILogger<DiscordPublisher> log)
    {
        _client = client;
        _store = store;
        _options = options.Value;
        _log = log;
    }

    public bool IsReady =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.Token) &&
        _client.ConnectionState == ConnectionState.Connected;

    public async Task<ArchivedAttachment?> ArchiveAsync(
        string lineMessageId,
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Token) || content.Length == 0)
        {
            return null;
        }

        var settings = await _store.Settings
            .Find(Builders<SettingsDoc>.Filter.Empty)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(settings?.ArchiveChannelId))
        {
            _log.LogDebug("Archive channel is not configured; attachment {FileName} is not archived", fileName);
            return null;
        }

        if (!ulong.TryParse(settings!.ArchiveChannelId, out var channelId))
        {
            _log.LogWarning("Archive channel id {ChannelId} is not a valid snowflake", settings.ArchiveChannelId);
            return null;
        }

        if (_client.GetChannel(channelId) is not SocketTextChannel channel)
        {
            _log.LogWarning("Archive channel {ChannelId} is missing or not a text channel", channelId);
            return null;
        }

        var message = await FindMessageAsync(lineMessageId, ct);
        var header = await BuildArchiveHeaderAsync(message, lineMessageId, ct);

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var sent = await channel.SendFileAsync(
                new FileAttachment(stream, fileName),
                header,
                allowedMentions: AllowedMentions.None);

            var uploaded = sent.Attachments.FirstOrDefault();
            return new ArchivedAttachment(
                lineMessageId,
                message?.MsgType ?? "file",
                fileName,
                contentType,
                content.Length,
                channelId.ToString(),
                sent.Id.ToString(),
                uploaded?.Id.ToString(),
                "archived");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to archive attachment {FileName} to channel {ChannelId}", fileName, channelId);
            return null;
        }
    }

    public async Task<IReadOnlyList<PostRef>> PublishAsync(
        ItemDoc item,
        IReadOnlyList<string> channelIds,
        IReadOnlyList<ArchivedAttachment> attachments,
        CancellationToken ct)
    {
        var posts = new List<PostRef>();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Token) || channelIds.Count == 0)
        {
            return posts;
        }

        var message = await FindMessageAsync(item.LineMessageId, ct);
        var embed = DiscordEmbeds.ItemCard(item, message?.SenderName, attachments);

        foreach (var channelId in channelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            if (!ulong.TryParse(channelId, out var id))
            {
                _log.LogWarning("Skipping channel {ChannelId}: not a valid snowflake", channelId);
                continue;
            }

            if (_client.GetChannel(id) is not SocketTextChannel channel)
            {
                _log.LogWarning("Skipping channel {ChannelId}: missing or not a text channel", channelId);
                continue;
            }

            try
            {
                var sent = await channel.SendMessageAsync(
                    text: null,
                    embeds: new[] { embed },
                    allowedMentions: AllowedMentions.None);

                posts.Add(new PostRef(channelId, sent.Id.ToString()));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to publish item {ItemId} ({Title}) to channel {ChannelId}",
                    item.Id, item.Title, channelId);
            }
        }

        return posts;
    }

    public async Task DeleteByLineMessageIdAsync(string lineMessageId, CancellationToken ct)
    {
        var posts = await _store.Posts
            .Find(Builders<PostDoc>.Filter.Eq(p => p.LineMessageId, lineMessageId))
            .ToListAsync(ct);

        foreach (var post in posts)
        {
            if (!ulong.TryParse(post.ChannelId, out var channelId) || !ulong.TryParse(post.DiscordMessageId, out var messageId))
            {
                _log.LogWarning("Skipping post with malformed ids (channel {ChannelId}, message {MessageId})",
                    post.ChannelId, post.DiscordMessageId);
                continue;
            }

            if (_client.GetChannel(channelId) is not IMessageChannel channel)
            {
                _log.LogWarning("Channel {ChannelId} is not cached; message {MessageId} not deleted", channelId, messageId);
                continue;
            }

            try
            {
                await channel.DeleteMessageAsync(messageId, new RequestOptions { CancelToken = ct });
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownMessage)
            {
                _log.LogDebug("Message {MessageId} was already deleted", messageId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete message {MessageId} in channel {ChannelId}", messageId, channelId);
            }
        }

        if (posts.Count > 0)
        {
            await _store.Posts.DeleteManyAsync(
                Builders<PostDoc>.Filter.Eq(p => p.LineMessageId, lineMessageId),
                ct);
        }
    }

    private async Task<MessageDoc?> FindMessageAsync(string lineMessageId, CancellationToken ct) =>
        await _store.Messages
            .Find(Builders<MessageDoc>.Filter.Eq(m => m.LineMessageId, lineMessageId))
            .FirstOrDefaultAsync(ct);

    private async Task<string> BuildArchiveHeaderAsync(MessageDoc? message, string lineMessageId, CancellationToken ct)
    {
        var parts = new List<string>();

        if (message is not null)
        {
            parts.Add($"原始时间 {message.SentAt:yyyy-MM-dd HH:mm} UTC");

            var groupName = message.LineGroupId;
            if (!string.IsNullOrWhiteSpace(message.LineGroupId))
            {
                var group = await _store.Groups
                    .Find(Builders<GroupDoc>.Filter.Eq(g => g.LineGroupId, message.LineGroupId))
                    .FirstOrDefaultAsync(ct);

                if (!string.IsNullOrWhiteSpace(group?.DisplayName))
                {
                    groupName = group!.DisplayName;
                }
            }

            if (!string.IsNullOrWhiteSpace(groupName))
            {
                parts.Add($"LINE群 {groupName}");
            }

            if (!string.IsNullOrWhiteSpace(message.SenderName))
            {
                parts.Add($"发送者 {message.SenderName}");
            }
        }

        if (parts.Count == 0)
        {
            parts.Add($"LINE 附件 {lineMessageId}");
        }

        return DiscordText.Clip(string.Join(" · ", parts), DiscordText.MessageMax - 100);
    }
}
