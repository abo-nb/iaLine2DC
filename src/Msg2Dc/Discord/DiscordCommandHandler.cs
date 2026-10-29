using System.Diagnostics;
using System.Text;
using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Search;
using Msg2Dc.Storage;

namespace Msg2Dc.Discord;

public sealed class DiscordCommandHandler
{
    private readonly MongoStore _store;
    private readonly JobQueue _queue;
    private readonly ILlmModelSwitcher _llm;
    private readonly RuntimeOptions _runtime;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordCommandHandler> _log;

    public DiscordCommandHandler(
        MongoStore store,
        JobQueue queue,
        ILlmModelSwitcher llm,
        RuntimeOptions runtime,
        DiscordSocketClient client,
        ILogger<DiscordCommandHandler> log)
    {
        _store = store;
        _queue = queue;
        _llm = llm;
        _runtime = runtime;
        _client = client;
        _log = log;
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var name = command.CommandName;

        if (DiscordCommands.AdminCommands.Contains(name) && !_runtime.IsAdmin(command.User.Id))
        {
            await ReplyAsync(command, "无权限", ephemeral: true);
            return;
        }

        var ephemeral = !DiscordCommands.PublicCommands.Contains(name);
        await DeferAsync(command, ephemeral);

        try
        {
            switch (name)
            {
                case "search":
                    await SearchAsync(command, ct);
                    break;
                case "latest":
                    await LatestAsync(command, ct);
                    break;
                case "stats":
                    await StatsAsync(command, ct);
                    break;
                case "subscribe":
                    await SubscribeAsync(command, ct);
                    break;
                case "unsubscribe":
                    await UnsubscribeAsync(command, ct);
                    break;
                case "subscriptions":
                    await SubscriptionsAsync(command, ct);
                    break;
                case "subject-add":
                    await SubjectAddAsync(command, ct);
                    break;
                case "subject-remove":
                    await SubjectRemoveAsync(command, ct);
                    break;
                case "subject-alias":
                    await SubjectAliasAsync(command, ct);
                    break;
                case "subjects":
                    await SubjectsAsync(command, ct);
                    break;
                case "set-archive":
                    await SetChannelAsync(command, archive: true, ct);
                    break;
                case "set-default":
                    await SetChannelAsync(command, archive: false, ct);
                    break;
                case "groups":
                    await GroupsAsync(command, ct);
                    break;
                case "group-bind":
                    await GroupBindAsync(command, ct);
                    break;
                case "set-model":
                    await SetModelAsync(command, ct);
                    break;
                case "reprocess":
                    await ReprocessAsync(command, ct);
                    break;
                case "status":
                    await StatusAsync(command, ct);
                    break;
                default:
                    await ReplyAsync(command, $"未知命令：/{name}", ephemeral: true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Slash command /{Command} was cancelled", name);
            await ReplyAsync(command, "处理超时，请稍后再试。", ephemeral);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Slash command /{Command} failed for user {UserId}", name, command.User.Id);
            await ReplyAsync(command, "命令执行失败，请稍后再试。", ephemeral);
        }
    }

    private async Task SearchAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var query = OptionString(command, "query");
        var tokens = Tokenizer.Tokenize(query);
        if (tokens.Count == 0)
        {
            await ReplyAsync(command, "请输入有效的搜索关键词。", ephemeral: false);
            return;
        }

        var filters = new List<FilterDefinition<ItemDoc>>
        {
            Builders<ItemDoc>.Filter.All(i => i.SearchTokens, tokens)
        };

        var subjectArg = OptionString(command, "subject");
        if (!string.IsNullOrWhiteSpace(subjectArg))
        {
            var subject = await FindSubjectAsync(subjectArg, ct);
            filters.Add(Builders<ItemDoc>.Filter.Eq(i => i.SubjectName, subject?.Name ?? subjectArg.Trim()));
        }

        var days = OptionInt(command, "days");
        if (days is > 0)
        {
            filters.Add(Builders<ItemDoc>.Filter.Gte(i => i.CreatedAt, DateTime.UtcNow.AddDays(-days.Value)));
        }

        var items = await _store.Items
            .Find(Builders<ItemDoc>.Filter.And(filters))
            .SortByDescending(i => i.CreatedAt)
            .Limit(10)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await ReplyAsync(command, $"没有找到与「{DiscordText.Clip(query, 80)}」匹配的内容。", ephemeral: false);
            return;
        }

        var posts = await LoadPostsAsync(items, ct);
        await ReplyAsync(command, BuildItemList("搜索结果", items, posts, command.GuildId), ephemeral: false);
    }

    private async Task LatestAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var subjectArg = OptionString(command, "subject");
        if (string.IsNullOrWhiteSpace(subjectArg))
        {
            await ReplyAsync(command, "请指定科目。", ephemeral: false);
            return;
        }

        var subject = await FindSubjectAsync(subjectArg, ct);
        var name = subject?.Name ?? subjectArg.Trim();
        var limit = Math.Clamp(OptionInt(command, "limit") ?? 10, 1, 20);

        var items = await _store.Items
            .Find(Builders<ItemDoc>.Filter.Eq(i => i.SubjectName, name))
            .SortByDescending(i => i.CreatedAt)
            .Limit(limit)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await ReplyAsync(command, $"科目「{name}」还没有内容。", ephemeral: false);
            return;
        }

        var posts = await LoadPostsAsync(items, ct);
        await ReplyAsync(command, BuildItemList($"{name} 最新 {items.Count} 条", items, posts, command.GuildId), ephemeral: false);
    }

    private async Task StatsAsync(SocketSlashCommand command, CancellationToken ct)
    {
        FieldDefinition<ItemDoc, string> field = new StringFieldDefinition<ItemDoc, string>("subjectName");
        using var cursor = await _store.Items.DistinctAsync(
            field,
            Builders<ItemDoc>.Filter.Empty,
            options: null,
            cancellationToken: ct);

        var names = (await cursor.ToListAsync(ct))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var counts = new List<(string Name, long Count)>();
        foreach (var name in names)
        {
            var count = await _store.Items.CountDocumentsAsync(
                Builders<ItemDoc>.Filter.Eq(i => i.SubjectName, name),
                cancellationToken: ct);
            counts.Add((name, count));
        }

        var latest = await _store.Items
            .Find(Builders<ItemDoc>.Filter.Empty)
            .SortByDescending(i => i.CreatedAt)
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        var pending = await _queue.PendingCountAsync(ct);

        var builder = new StringBuilder();
        builder.Append("**收集统计**\n");
        builder.Append("共 ").Append(counts.Sum(c => c.Count)).Append(" 条内容，").Append(counts.Count).Append(" 个科目\n");

        foreach (var (name, count) in counts.OrderByDescending(c => c.Count))
        {
            builder.Append("• ").Append(name).Append("：").Append(count).Append(" 条\n");
        }

        if (latest is null)
        {
            builder.Append("最近一条：暂无\n");
        }
        else
        {
            builder.Append("最近一条：").Append(DiscordText.Relative(latest.CreatedAt))
                .Append(" · ").Append(latest.SubjectName).Append('\n');
        }

        builder.Append("队列待处理：").Append(pending).Append(" 个");

        await ReplyAsync(command, builder.ToString(), ephemeral: false);
    }

    private async Task SubscribeAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.GuildId is not ulong guildId || command.ChannelId is not ulong channelId)
        {
            await ReplyAsync(command, "该命令只能在服务器频道中使用。", ephemeral: true);
            return;
        }

        var subjectArg = OptionString(command, "subject");
        if (string.IsNullOrWhiteSpace(subjectArg))
        {
            await ReplyAsync(command, "请指定科目。", ephemeral: true);
            return;
        }

        var subject = await EnsureSubjectAsync(subjectArg, ct);

        await _store.Subscriptions.UpdateOneAsync(
            Builders<SubscriptionDoc>.Filter.And(
                Builders<SubscriptionDoc>.Filter.Eq(s => s.ChannelId, channelId.ToString()),
                Builders<SubscriptionDoc>.Filter.Eq(s => s.SubjectName, subject.Name)),
            Builders<SubscriptionDoc>.Update
                .SetOnInsert(s => s.GuildId, guildId.ToString())
                .SetOnInsert(s => s.ChannelId, channelId.ToString())
                .SetOnInsert(s => s.SubjectName, subject.Name)
                .SetOnInsert(s => s.CreatedBy, command.User.Id.ToString())
                .SetOnInsert(s => s.CreatedAt, DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true },
            ct);

        var channelCount = await _store.Subscriptions.CountDocumentsAsync(
            Builders<SubscriptionDoc>.Filter.Eq(s => s.SubjectName, subject.Name),
            cancellationToken: ct);

        await ReplyAsync(command,
            $"本频道已订阅科目 **{subject.Name}**，该科目共有 {channelCount} 个频道订阅。",
            ephemeral: true);
    }

    private async Task UnsubscribeAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.ChannelId is not ulong channelId)
        {
            await ReplyAsync(command, "该命令只能在服务器频道中使用。", ephemeral: true);
            return;
        }

        var subjectArg = OptionString(command, "subject");
        if (string.IsNullOrWhiteSpace(subjectArg))
        {
            await ReplyAsync(command, "请指定科目。", ephemeral: true);
            return;
        }

        var subject = await FindSubjectAsync(subjectArg, ct);
        var name = subject?.Name ?? subjectArg.Trim();

        var result = await _store.Subscriptions.DeleteOneAsync(
            Builders<SubscriptionDoc>.Filter.And(
                Builders<SubscriptionDoc>.Filter.Eq(s => s.ChannelId, channelId.ToString()),
                Builders<SubscriptionDoc>.Filter.Eq(s => s.SubjectName, name)),
            ct);

        await ReplyAsync(command,
            result.DeletedCount > 0 ? $"本频道已取消订阅科目 **{name}**。" : $"本频道没有订阅科目 **{name}**。",
            ephemeral: true);
    }

    private async Task SubscriptionsAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.ChannelId is not ulong channelId)
        {
            await ReplyAsync(command, "该命令只能在服务器频道中使用。", ephemeral: true);
            return;
        }

        var subscriptions = await _store.Subscriptions
            .Find(Builders<SubscriptionDoc>.Filter.Eq(s => s.ChannelId, channelId.ToString()))
            .SortBy(s => s.SubjectName)
            .ToListAsync(ct);

        if (subscriptions.Count == 0)
        {
            await ReplyAsync(command, "本频道尚未订阅任何科目，使用 /subscribe 添加。", ephemeral: true);
            return;
        }

        var builder = new StringBuilder();
        builder.Append("**本频道订阅的科目**\n");
        foreach (var subscription in subscriptions)
        {
            builder.Append("• ").Append(subscription.SubjectName).Append('\n');
        }

        await ReplyAsync(command, builder.ToString().TrimEnd(), ephemeral: true);
    }

    private async Task SubjectAddAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var name = OptionString(command, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await ReplyAsync(command, "请提供科目名称。", ephemeral: true);
            return;
        }

        var existing = await FindSubjectAsync(name, ct);
        if (existing is not null)
        {
            await ReplyAsync(command,
                $"科目已存在：**{existing.Name}**（可用 /subject-alias 追加别名）",
                ephemeral: true);
            return;
        }

        var subject = new SubjectDoc
        {
            Name = name,
            Aliases = ParseAliases(OptionString(command, "aliases"), name),
            Enabled = true,
            Order = await NextSubjectOrderAsync(ct),
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _store.Subjects.InsertOneAsync(subject, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
        {
            await ReplyAsync(command, $"科目已存在：**{name}**", ephemeral: true);
            return;
        }

        var aliases = subject.Aliases.Count > 0 ? $"，别名：{string.Join("、", subject.Aliases)}" : "";
        await ReplyAsync(command, $"已新增科目 **{subject.Name}**{aliases}", ephemeral: true);
    }

    private async Task SubjectRemoveAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var name = OptionString(command, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await ReplyAsync(command, "请提供科目名称。", ephemeral: true);
            return;
        }

        var subject = await FindSubjectAsync(name, ct);
        if (subject is null)
        {
            await ReplyAsync(command, $"找不到科目 **{name}**。", ephemeral: true);
            return;
        }

        await _store.Subjects.DeleteOneAsync(
            Builders<SubjectDoc>.Filter.Eq(s => s.Id, subject.Id),
            ct);

        var removed = await _store.Subscriptions.DeleteManyAsync(
            Builders<SubscriptionDoc>.Filter.Eq(s => s.SubjectName, subject.Name),
            ct);

        await ReplyAsync(command,
            $"已删除科目 **{subject.Name}**，同时移除 {removed.DeletedCount} 个频道订阅。",
            ephemeral: true);
    }

    private async Task SubjectAliasAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var name = OptionString(command, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await ReplyAsync(command, "请提供科目名称。", ephemeral: true);
            return;
        }

        var subject = await FindSubjectAsync(name, ct);
        if (subject is null)
        {
            await ReplyAsync(command, $"找不到科目 **{name}**。", ephemeral: true);
            return;
        }

        var aliases = ParseAliases(OptionString(command, "aliases"), subject.Name);
        if (aliases.Count == 0)
        {
            await ReplyAsync(command, "请提供至少一个别名。", ephemeral: true);
            return;
        }

        var merged = (subject.Aliases ?? new List<string>())
            .Concat(aliases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _store.Subjects.UpdateOneAsync(
            Builders<SubjectDoc>.Filter.Eq(s => s.Id, subject.Id),
            Builders<SubjectDoc>.Update.Set(s => s.Aliases, merged),
            cancellationToken: ct);

        await ReplyAsync(command,
            $"科目 **{subject.Name}** 的别名：{string.Join("、", merged)}",
            ephemeral: true);
    }

    private async Task SubjectsAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var subjects = await _store.Subjects
            .Find(Builders<SubjectDoc>.Filter.Empty)
            .SortBy(s => s.Order)
            .ToListAsync(ct);

        if (subjects.Count == 0)
        {
            await ReplyAsync(command, "还没有任何科目，使用 /subject-add 添加。", ephemeral: true);
            return;
        }

        var builder = new StringBuilder();
        builder.Append("**科目（").Append(subjects.Count).Append("）**\n");
        foreach (var subject in subjects)
        {
            builder.Append("• ").Append(subject.Name);
            if (subject.Aliases is { Count: > 0 })
            {
                builder.Append(" · 别名：").Append(string.Join("、", subject.Aliases));
            }

            if (!subject.Enabled)
            {
                builder.Append("（已停用）");
            }

            builder.Append('\n');
        }

        await ReplyAsync(command, builder.ToString().TrimEnd(), ephemeral: true);
    }

    private async Task SetChannelAsync(SocketSlashCommand command, bool archive, CancellationToken ct)
    {
        var channelId = OptionChannelId(command, "channel");
        if (channelId is null || _client.GetChannel(channelId.Value) is not SocketTextChannel channel)
        {
            await ReplyAsync(command, "找不到该频道，或它不是文字频道。", ephemeral: true);
            return;
        }

        var update = archive
            ? Builders<SettingsDoc>.Update.Set(s => s.ArchiveChannelId, channel.Id.ToString())
            : Builders<SettingsDoc>.Update.Set(s => s.DefaultChannelId, channel.Id.ToString());

        update = update
            .Set(s => s.UpdatedAt, DateTime.UtcNow)
            .Set(s => s.UpdatedBy, command.User.Id.ToString());

        await _store.Settings.UpdateOneAsync(
            Builders<SettingsDoc>.Filter.Empty,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);

        await ReplyAsync(command,
            archive ? $"已设置归档频道：<#{channel.Id}>" : $"已设置默认推送频道：<#{channel.Id}>",
            ephemeral: true);
    }

    private async Task GroupsAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var groups = await _store.Groups
            .Find(Builders<GroupDoc>.Filter.Empty)
            .SortByDescending(g => g.UpdatedAt)
            .Limit(20)
            .ToListAsync(ct);

        if (groups.Count == 0)
        {
            await ReplyAsync(command, "还没有已知的 LINE 群，机器人被拉入群后会自动登记。", ephemeral: true);
            return;
        }

        var builder = new StringBuilder();
        builder.Append("**LINE 群（").Append(groups.Count).Append("）**\n");
        foreach (var group in groups)
        {
            builder.Append("• ").Append(string.IsNullOrWhiteSpace(group.DisplayName) ? "（未命名）" : group.DisplayName)
                .Append(" — `").Append(group.LineGroupId).Append('`')
                .Append(" · 科目 ").Append(string.IsNullOrWhiteSpace(group.SubjectName) ? "未绑定" : group.SubjectName);

            if (!group.Active)
            {
                builder.Append("（已停用）");
            }

            builder.Append('\n');
        }

        builder.Append("使用 /group-bind 绑定群与科目。");
        await ReplyAsync(command, builder.ToString(), ephemeral: true);
    }

    private async Task GroupBindAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var groupId = OptionString(command, "group")?.Trim();
        var subjectArg = OptionString(command, "subject")?.Trim();
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(subjectArg))
        {
            await ReplyAsync(command, "请提供 LINE 群 ID 与科目。", ephemeral: true);
            return;
        }

        var subject = await EnsureSubjectAsync(subjectArg, ct);

        var result = await _store.Groups.UpdateOneAsync(
            Builders<GroupDoc>.Filter.Eq(g => g.LineGroupId, groupId),
            Builders<GroupDoc>.Update
                .Set(g => g.SubjectName, subject.Name)
                .Set(g => g.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

        if (result.MatchedCount == 0)
        {
            await ReplyAsync(command, $"找不到 LINE 群 `{groupId}`，请先用 /groups 查看。", ephemeral: true);
            return;
        }

        await ReplyAsync(command, $"LINE 群 `{groupId}` 已绑定科目 **{subject.Name}**。", ephemeral: true);
    }

    private async Task SetModelAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var provider = OptionString(command, "provider")?.Trim();
        var model = OptionString(command, "model")?.Trim();
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            await ReplyAsync(command, "请提供提供方与模型名称。", ephemeral: true);
            return;
        }

        var (ok, message) = await _llm.SetModelAsync(provider, model, ct);
        var text = string.IsNullOrWhiteSpace(message)
            ? (ok ? $"已切换到 {provider}/{model}。" : $"切换到 {provider}/{model} 失败。")
            : message;

        await ReplyAsync(command, text, ephemeral: true);
    }

    private async Task ReprocessAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var lineMessageId = OptionString(command, "message_id")?.Trim();
        if (string.IsNullOrWhiteSpace(lineMessageId))
        {
            await ReplyAsync(command, "请提供 LINE 消息 ID。", ephemeral: true);
            return;
        }

        var result = await _store.Messages.UpdateOneAsync(
            Builders<MessageDoc>.Filter.Eq(m => m.LineMessageId, lineMessageId),
            Builders<MessageDoc>.Update.Set(m => m.ProcessedAt, (DateTime?)null),
            cancellationToken: ct);

        if (result.MatchedCount == 0)
        {
            await ReplyAsync(command, $"找不到消息 `{lineMessageId}`。", ephemeral: true);
            return;
        }

        await _queue.EnqueueProcessAsync(lineMessageId, TimeSpan.FromSeconds(1), ct);
        await ReplyAsync(command, $"消息 `{lineMessageId}` 已重新排队，稍后重新分类并推送。", ephemeral: true);
    }

    private async Task StatusAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var (provider, model) = _llm.Current;
        var pending = await _queue.PendingCountAsync(ct);

        var stopwatch = Stopwatch.StartNew();
        var mongoOk = await _store.PingAsync(TimeSpan.FromSeconds(3), ct);
        stopwatch.Stop();

        var lastLog = await _store.LlmLogs
            .Find(Builders<LlmLogDoc>.Filter.Empty)
            .SortByDescending(l => l.CreatedAt)
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        var builder = new StringBuilder();
        builder.Append("**运行状态**\n");
        builder.Append("模型：").Append(string.IsNullOrWhiteSpace(provider) ? "未配置" : provider)
            .Append(" / ").Append(string.IsNullOrWhiteSpace(model) ? "未配置" : model).Append('\n');
        builder.Append("网关：").Append(ConnectionLabel(_client.ConnectionState))
            .Append(" · 延迟 ").Append(_client.Latency).Append("ms\n");
        builder.Append("队列待处理：").Append(pending).Append(" 个\n");
        builder.Append("MongoDB：").Append(mongoOk ? $"正常（{stopwatch.ElapsedMilliseconds}ms）" : "不可用").Append('\n');

        if (lastLog is null)
        {
            builder.Append("最近 LLM 调用：无");
        }
        else
        {
            builder.Append("最近 LLM 调用：").Append(DiscordText.Relative(lastLog.CreatedAt))
                .Append(" · ").Append(lastLog.Provider).Append('/').Append(lastLog.Model)
                .Append(lastLog.Ok ? " · 成功" : " · 失败");

            if (lastLog.PromptTokens is not null || lastLog.CompletionTokens is not null)
            {
                builder.Append(" · 输入 ").Append(lastLog.PromptTokens ?? 0)
                    .Append(" / 输出 ").Append(lastLog.CompletionTokens ?? 0);
            }

            if (!lastLog.Ok && !string.IsNullOrWhiteSpace(lastLog.Error))
            {
                builder.Append("\n最近错误：").Append(DiscordText.Clip(lastLog.Error, 300));
            }
        }

        await ReplyAsync(command, builder.ToString(), ephemeral: true);
    }

    private static string ConnectionLabel(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Disconnecting => "断开中",
        _ => "未连接"
    };

    private async Task<IReadOnlyDictionary<string, PostDoc>> LoadPostsAsync(IReadOnlyList<ItemDoc> items, CancellationToken ct)
    {
        var ids = items
            .Select(i => i.LineMessageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<string, PostDoc>();
        }

        var posts = await _store.Posts
            .Find(Builders<PostDoc>.Filter.In(p => p.LineMessageId, ids))
            .ToListAsync(ct);

        return posts
            .GroupBy(p => p.LineMessageId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreatedAt).First());
    }

    private string BuildItemList(
        string heading,
        IReadOnlyList<ItemDoc> items,
        IReadOnlyDictionary<string, PostDoc> posts,
        ulong? fallbackGuildId)
    {
        var builder = new StringBuilder();
        builder.Append("**").Append(heading).Append("**\n");

        foreach (var item in items)
        {
            var title = DiscordText.Clip(item.Title, 80);
            if (title.Length == 0)
            {
                title = "（无标题）";
            }

            builder.Append("• **").Append(title).Append("** · ").Append(item.SubjectName)
                .Append(" · ").Append(DiscordText.Relative(item.CreatedAt));

            var link = BuildPostLink(item.LineMessageId, posts, fallbackGuildId);
            if (link is not null)
            {
                builder.Append(" · [查看](").Append(link).Append(')');
            }

            builder.Append('\n');
        }

        return DiscordText.Clip(builder.ToString().TrimEnd(), DiscordText.MessageMax - 100);
    }

    private string? BuildPostLink(
        string lineMessageId,
        IReadOnlyDictionary<string, PostDoc> posts,
        ulong? fallbackGuildId)
    {
        if (!posts.TryGetValue(lineMessageId, out var post))
        {
            return null;
        }

        if (!ulong.TryParse(post.ChannelId, out var channelId) || !ulong.TryParse(post.DiscordMessageId, out var messageId))
        {
            return null;
        }

        var guildId = _client.GetChannel(channelId) is IGuildChannel guildChannel
            ? guildChannel.GuildId
            : fallbackGuildId;

        return guildId is null ? null : DiscordText.JumpLink(guildId.Value, channelId, messageId);
    }

    private async Task<SubjectDoc?> FindSubjectAsync(string nameOrAlias, CancellationToken ct)
    {
        var subjects = await _store.Subjects
            .Find(Builders<SubjectDoc>.Filter.Empty)
            .ToListAsync(ct);

        return subjects.FirstOrDefault(s =>
            string.Equals(s.Name, nameOrAlias, StringComparison.OrdinalIgnoreCase) ||
            (s.Aliases is not null && s.Aliases.Any(a => string.Equals(a, nameOrAlias, StringComparison.OrdinalIgnoreCase))));
    }

    private async Task<SubjectDoc> EnsureSubjectAsync(string nameOrAlias, CancellationToken ct)
    {
        var existing = await FindSubjectAsync(nameOrAlias, ct);
        if (existing is not null)
        {
            return existing;
        }

        var subject = new SubjectDoc
        {
            Name = nameOrAlias.Trim(),
            Enabled = true,
            Order = await NextSubjectOrderAsync(ct),
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _store.Subjects.InsertOneAsync(subject, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
        {
            return await FindSubjectAsync(nameOrAlias, ct) ?? subject;
        }

        return subject;
    }

    private async Task<int> NextSubjectOrderAsync(CancellationToken ct)
    {
        var last = await _store.Subjects
            .Find(Builders<SubjectDoc>.Filter.Empty)
            .SortByDescending(s => s.Order)
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        return (last?.Order ?? 0) + 1;
    }

    private static List<string> ParseAliases(string? raw, string subjectName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split(new[] { ' ', ',', '，', '、', ';', '；', '|' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(alias => !string.Equals(alias, subjectName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SocketSlashCommandDataOption? FindOption(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(option => string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string? OptionString(SocketSlashCommand command, string name) =>
        FindOption(command, name)?.Value as string;

    private static int? OptionInt(SocketSlashCommand command, string name)
    {
        var value = FindOption(command, name)?.Value;
        return value switch
        {
            long number => (int)number,
            int number => number,
            double number => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static ulong? OptionChannelId(SocketSlashCommand command, string name)
    {
        var value = FindOption(command, name)?.Value;
        return value switch
        {
            ulong id => id,
            long id when id > 0 => (ulong)id,
            string text when ulong.TryParse(text, out var parsed) => parsed,
            IChannel channel => channel.Id,
            _ => null
        };
    }

    private async Task DeferAsync(SocketSlashCommand command, bool ephemeral)
    {
        if (command.HasResponded)
        {
            return;
        }

        try
        {
            await command.DeferAsync(ephemeral);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to defer /{Command}", command.CommandName);
        }
    }

    private static async Task ReplyAsync(SocketSlashCommand command, string text, bool ephemeral)
    {
        var content = DiscordText.Clip(text, DiscordText.MessageMax - 100);

        if (command.HasResponded)
        {
            await command.FollowupAsync(content, ephemeral: ephemeral, allowedMentions: AllowedMentions.None);
            return;
        }

        await command.RespondAsync(content, ephemeral: ephemeral, allowedMentions: AllowedMentions.None);
    }
}
