using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Storage;

namespace Msg2Dc.Discord;

public sealed class DiscordPublisher : IDiscordPublisher
{
    private const int ForumTitleMax = 100;
    private const int MaxForumTags = 20;

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

    public bool IsReady =>        _options.Enabled &&
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
                options: WriteOptions(ct),
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
        catch (RateLimitedException ex)
        {
            _log.LogWarning(
                "Discord rate limited while archiving {FileName} to channel {ChannelId}: {Message}",
                fileName, channelId, ex.Message);
            return null;
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

            if (_client.GetChannel(id) is SocketForumChannel forum)
            {
                var threadId = await CreateForumPostAsync(forum, item, embed, ct);
                if (threadId is ulong created)
                {
                    posts.Add(new PostRef(channelId, created.ToString()));
                }

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
                    options: WriteOptions(ct),
                    allowedMentions: AllowedMentions.None);

                posts.Add(new PostRef(channelId, sent.Id.ToString()));
            }
            catch (RateLimitedException ex)
            {
                _log.LogWarning(
                    "Discord rate limited publishing item {ItemId} to channel {ChannelId}: {Message}",
                    item.Id, channelId, ex.Message);
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

            // Forum posts are threads: the stored id is the thread id, not a message id.
            if (_client.GetChannel(messageId) is SocketThreadChannel thread)
            {
                try
                {
                    await thread.DeleteAsync(WriteOptions(ct));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete forum post (thread) {ThreadId}", messageId);
                }

                continue;
            }

            if (_client.GetChannel(channelId) is not IMessageChannel channel)
            {
                _log.LogWarning("Channel {ChannelId} is not cached; message {MessageId} not deleted", channelId, messageId);
                continue;
            }

            try
            {
                await channel.DeleteMessageAsync(messageId, WriteOptions(ct));
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

    /// <summary>
    /// Forum channels accept "posts" (threads) instead of messages. The title is what the
    /// class will see in the list, so use the item title; tags are matched/created from the
    /// subject and kind.
    /// </summary>
    private async Task<ulong?> CreateForumPostAsync(
        SocketForumChannel forum,
        ItemDoc item,
        Embed embed,
        CancellationToken ct)
    {
        var title = DiscordText.Clip(item.Title, ForumTitleMax);
        if (title.Length == 0)
        {
            title = DiscordEmbeds.KindLabel(item.Kind);
        }

        var text = DiscordText.Clip(item.Summary, DiscordText.MessageMax - 100);
        if (text.Length == 0)
        {
            text = DiscordEmbeds.KindLabel(item.Kind);
        }

        try
        {
            var tags = await ResolveForumTagsAsync(forum, item, ct);

            var thread = await forum.CreatePostAsync(
                title,
                archiveDuration: ThreadArchiveDuration.OneWeek,
                text: text,
                embeds: new[] { embed },
                options: WriteOptions(ct),
                allowedMentions: AllowedMentions.None,
                tags: tags);

            _log.LogInformation(
                "Published item {ItemId} as forum post {ThreadId} in {ChannelId} with {TagCount} tag(s)",
                item.Id, thread.Id, forum.Id, tags.Length);

            return thread.Id;
        }
        catch (RateLimitedException ex)
        {
            _log.LogWarning("Discord rate limited creating forum post in {ChannelId}: {Message}", forum.Id, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create forum post for item {ItemId} in channel {ChannelId}",
                item.Id, forum.Id);
            return null;
        }
    }

    /// <summary>
    /// Applies matching forum tags for the subject (its name or any of its aliases, so a
    /// forum that names tags in traditional Chinese still matches a simplified subject), the
    /// kind and any item tag that already exists. Missing subject/kind tags are created when
    /// the forum is below Discord's 20-tag cap and the bot may manage the channel.
    /// </summary>
    private async Task<ForumTag[]> ResolveForumTagsAsync(SocketForumChannel forum, ItemDoc item, CancellationToken ct)
    {
        var wanted = new List<(string Name, bool Create, IReadOnlyList<string> Candidates)>();

        if (!string.IsNullOrWhiteSpace(item.SubjectName) &&
            !string.Equals(item.SubjectName, "other", StringComparison.OrdinalIgnoreCase))
        {
            var subject = await FindSubjectAsync(item.SubjectName, ct);
            var candidates = new List<string> { item.SubjectName };
            if (subject?.Aliases is { Count: > 0 })
            {
                candidates.AddRange(subject.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)));
            }

            wanted.Add((
                DiscordText.Clip(item.SubjectName, 20),
                true,
                candidates.Select(n => DiscordText.Clip(n, 20)).Distinct(StringComparer.Ordinal).ToList()));
        }

        var kindLabel = DiscordEmbeds.KindLabel(item.Kind);
        wanted.Add((kindLabel, true, new[] { kindLabel }));

        foreach (var tag in item.Tags ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var name = DiscordText.Clip(tag, 20);
            wanted.Add((name, false, new[] { name }));
        }

        var existing = forum.Tags.ToList();
        var byName = existing
            .GroupBy(t => NormalizeTagName(t.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var matched = new List<ForumTag>();
        var missing = new List<string>();
        foreach (var (name, create, candidates) in wanted)
        {
            ForumTag? hit = null;
            foreach (var candidate in candidates)
            {
                if (byName.TryGetValue(NormalizeTagName(candidate), out var tag))
                {
                    hit = tag;
                    break;
                }
            }

            if (hit.HasValue)
            {
                var tag = hit.Value;
                if (matched.All(t => t.Id != tag.Id))
                {
                    matched.Add(tag);
                }
            }
            else if (create && !missing.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(name);
            }
        }

        if (missing.Count == 0 || existing.Count + missing.Count > MaxForumTags)
        {
            return matched.ToArray();
        }

        try
        {
            var tags = existing.Cast<IForumTag>().ToList();
            foreach (var name in missing)
            {
                tags.Add(new ForumTagBuilder { Name = DiscordText.Clip(name, 20) }.Build());
            }

            await forum.ModifyAsync(x => x.Tags = tags, WriteOptions(ct));

            foreach (var tag in await FetchForumTagsAsync(forum, ct))
            {
                if (missing.Contains(tag.Name, StringComparer.OrdinalIgnoreCase) && matched.All(t => t.Id != tag.Id))
                {
                    matched.Add(tag);
                }
            }

            _log.LogInformation(
                "Created forum tag(s) {Tags} in channel {ChannelId}",
                string.Join(", ", missing), forum.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create forum tags in channel {ChannelId}; posting without them", forum.Id);
        }

        return matched.ToArray();
    }

    /// <summary>
    /// <see cref="SocketForumChannel.ModifyAsync"/> does not refresh the cached tag list, so
    /// the new tags (and their snowflakes) have to be read back from REST.
    /// </summary>
    private async Task<IReadOnlyCollection<ForumTag>> FetchForumTagsAsync(SocketForumChannel forum, CancellationToken ct)
    {
        try
        {
            if (await _client.Rest.GetChannelAsync(forum.Id, WriteOptions(ct)) is RestForumChannel channel)
            {
                return channel.Tags;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not refresh forum tags for channel {ChannelId}", forum.Id);
        }

        return forum.Tags;
    }

    private async Task<SubjectDoc?> FindSubjectAsync(string name, CancellationToken ct) =>
        await _store.Subjects
            .Find(s => s.Name == name || s.Aliases.Contains(name))
            .FirstOrDefaultAsync(ct);

    private static string NormalizeTagName(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Discord.Net's default RetryMode retries a 429 in a tight loop; fail fast instead
    /// so the caller (and the rate-limit guard) can back off properly.
    /// </summary>
    private static RequestOptions WriteOptions(CancellationToken ct) =>
        new() { RetryMode = RetryMode.Retry502, Timeout = 15000, CancelToken = ct };
}
