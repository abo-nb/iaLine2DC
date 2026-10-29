using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Storage;

namespace Msg2Dc.Pipeline;

public sealed class MessageWorker : BackgroundService
{
    private readonly MongoStore _store;
    private readonly JobQueue _queue;
    private readonly ILlmClassifier _classifier;
    private readonly IDiscordPublisher _publisher;
    private readonly Line.LineApiClient _line;
    private readonly PipelineOptions _pipeline;
    private readonly ILogger<MessageWorker> _log;

    public MessageWorker(
        MongoStore store,
        JobQueue queue,
        ILlmClassifier classifier,
        IDiscordPublisher publisher,
        Line.LineApiClient line,
        IOptions<PipelineOptions> pipeline,
        ILogger<MessageWorker> log)
    {
        _store = store;
        _queue = queue;
        _classifier = classifier;
        _publisher = publisher;
        _line = line;
        _pipeline = pipeline.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var loops = Enumerable.Range(0, Math.Max(1, _pipeline.WorkerCount))
            .Select(_ => Task.Run(() => LoopAsync(stoppingToken), stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JobDoc? job;
            try
            {
                job = await _queue.ClaimAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to claim job");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            try
            {
                await ProcessAsync(job, ct);
                await _queue.CompleteAsync(job.Id, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job {JobId} ({Kind}) failed", job.Id, job.Kind);
                await _queue.FailAsync(job, ex.Message, ct);
            }
        }
    }

    private Task ProcessAsync(JobDoc job, CancellationToken ct) => job.Kind switch
    {
        "delete_posts" => _publisher.DeleteByLineMessageIdAsync(job.LineMessageId, ct),
        _ => ProcessMessageAsync(job, ct)
    };

    private async Task ProcessMessageAsync(JobDoc job, CancellationToken ct)
    {
        var message = await _store.Messages
            .Find(Builders<MessageDoc>.Filter.Eq(m => m.LineMessageId, job.LineMessageId))
            .FirstOrDefaultAsync(ct);

        if (message is null || message.DeletedAt is not null)
        {
            return;
        }

        var group = await _store.Groups
            .Find(Builders<GroupDoc>.Filter.Eq(g => g.LineGroupId, message.LineGroupId))
            .FirstOrDefaultAsync(ct);

        var batch = await CollectBatchAsync(message, ct);
        var text = string.Join("\n", batch.Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()));
        var attachments = await ArchiveAttachmentsAsync(batch, ct);

        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            await MarkProcessedAsync(batch, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.SenderName) && !string.IsNullOrEmpty(message.SenderUserId))
        {
            var name = await _line.GetGroupMemberNameAsync(message.LineGroupId, message.SenderUserId, ct);
            if (!string.IsNullOrWhiteSpace(name))
            {
                message.SenderName = name;
                await _store.Messages.UpdateOneAsync(
                    Builders<MessageDoc>.Filter.Eq(m => m.Id, message.Id),
                    Builders<MessageDoc>.Update.Set(m => m.SenderName, name),
                    cancellationToken: ct);
            }
        }

        var subjects = await _store.Subjects
            .Find(Builders<SubjectDoc>.Filter.Eq(s => s.Enabled, true))
            .SortBy(s => s.Order)
            .ToListAsync(ct);

        var request = new ClassifyRequest(
            group?.SubjectName,
            message.SenderName ?? "同学",
            message.SentAt,
            text,
            subjects.Select(s => new SubjectInfo(s.Name, s.Aliases)).ToList(),
            attachments.Select(a => a.FileName ?? a.Kind).ToList(),
            message.LineMessageId);

        var result = await _classifier.ClassifyAsync(request, ct);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"LLM classification failed: {result.Error}");
        }

        var items = new List<ItemDoc>();
        foreach (var classified in result.Items)
        {
            var now = DateTime.UtcNow;
            var item = new ItemDoc
            {
                MessageId = message.Id,
                LineMessageId = message.LineMessageId,
                LineGroupId = message.LineGroupId,
                SubjectName = ResolveSubject(classified.SubjectName, subjects),
                Kind = classified.Kind,
                Title = classified.Title,
                Summary = classified.Summary,
                BodyMd = classified.BodyMd,
                Tags = classified.Tags.ToList(),
                DeadlineAt = classified.DeadlineAt,
                Links = classified.Links.ToList(),
                Confidence = classified.Confidence,
                Status = classified.Confidence < _pipeline.MinConfidence ? "needs_review" : "published",
                CreatedAt = now,
                UpdatedAt = now
            };

            item.SearchTokens = Search.Tokenizer.Tokenize($"{item.Title} {item.Summary} {string.Join(' ', item.Tags)}");
            items.Add(item);
            await _store.Items.InsertOneAsync(item, cancellationToken: ct);

            var channelIds = await ResolveChannelsAsync(item, group, ct);
            if (channelIds.Count == 0)
            {
                _log.LogDebug("No Discord channel subscribed for subject {Subject}", item.SubjectName);
                continue;
            }

            var posts = await _publisher.PublishAsync(item, channelIds, attachments, ct);
            if (posts.Count > 0)
            {
                await _store.Posts.InsertManyAsync(posts.Select(p => new PostDoc
                {
                    ItemId = item.Id,
                    LineMessageId = item.LineMessageId,
                    ChannelId = p.ChannelId,
                    DiscordMessageId = p.MessageId,
                    CreatedAt = DateTime.UtcNow
                }), cancellationToken: ct);
            }
        }

        await MarkProcessedAsync(batch, ct);
        _log.LogInformation("Processed {MessageId}: {Count} item(s)", message.LineMessageId, items.Count);
    }

    private async Task<List<MessageDoc>> CollectBatchAsync(MessageDoc seed, CancellationToken ct)
    {
        var cutoff = seed.SentAt.AddSeconds(-60);
        var filter = Builders<MessageDoc>.Filter.And(
            Builders<MessageDoc>.Filter.Eq(m => m.LineGroupId, seed.LineGroupId),
            Builders<MessageDoc>.Filter.Eq(m => m.SenderUserId, seed.SenderUserId),
            Builders<MessageDoc>.Filter.Eq(m => m.ProcessedAt, null),
            Builders<MessageDoc>.Filter.Eq(m => m.DeletedAt, null),
            Builders<MessageDoc>.Filter.Gte(m => m.SentAt, cutoff),
            Builders<MessageDoc>.Filter.Lte(m => m.SentAt, DateTime.UtcNow));

        var batch = await _store.Messages.Find(filter).SortBy(m => m.SentAt).Limit(10).ToListAsync(ct);
        return batch.Count == 0 ? new List<MessageDoc> { seed } : batch;
    }

    private async Task<List<ArchivedAttachment>> ArchiveAttachmentsAsync(List<MessageDoc> batch, CancellationToken ct)
    {
        var archived = new List<ArchivedAttachment>();
        foreach (var message in batch.Where(m => m.MsgType != "text"))
        {
            var content = await _line.DownloadContentAsync(message.LineMessageId, ct);
            if (content is null || content.Length == 0)
            {
                archived.Add(new ArchivedAttachment(message.LineMessageId, message.MsgType, null, null, 0,
                    null, null, null, "download_failed"));
                continue;
            }

            var fileName = $"{message.LineMessageId}.{GuessExtension(message.MsgType)}";
            var stored = await _publisher.ArchiveAsync(message.LineMessageId, fileName, GuessContentType(message.MsgType), content, ct);
            archived.Add(stored ?? new ArchivedAttachment(message.LineMessageId, message.MsgType, fileName,
                GuessContentType(message.MsgType), content.Length, null, null, null, "media_lost"));

            var doc = archived[^1];
            await _store.Messages.UpdateOneAsync(
                Builders<MessageDoc>.Filter.Eq(m => m.Id, message.Id),
                Builders<MessageDoc>.Update.Set(m => m.Attachments, new List<AttachmentDoc>
                {
                    new()
                    {
                        LineMessageId = doc.LineMessageId,
                        Kind = doc.Kind,
                        FileName = doc.FileName,
                        ContentType = doc.ContentType,
                        Size = doc.Size,
                        ArchiveChannelId = doc.ArchiveChannelId,
                        ArchiveMessageId = doc.ArchiveMessageId,
                        ArchiveAttachmentId = doc.ArchiveAttachmentId,
                        Status = doc.Status,
                        SavedAt = DateTime.UtcNow
                    }
                }),
                cancellationToken: ct);
        }

        return archived;
    }

    private async Task<List<string>> ResolveChannelsAsync(ItemDoc item, GroupDoc? group, CancellationToken ct)
    {
        var subscriptions = await _store.Subscriptions
            .Find(Builders<SubscriptionDoc>.Filter.Eq(s => s.SubjectName, item.SubjectName))
            .ToListAsync(ct);

        var channels = subscriptions.Select(s => s.ChannelId).Distinct().ToList();
        if (channels.Count > 0)
        {
            return channels;
        }

        var settings = await _store.Settings.Find(_ => true).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(settings?.DefaultChannelId))
        {
            return new List<string> { settings.DefaultChannelId };
        }

        return new List<string>();
    }

    private Task MarkProcessedAsync(List<MessageDoc> batch, CancellationToken ct)
    {
        var ids = batch.Select(m => m.Id).ToList();
        return _store.Messages.UpdateManyAsync(
            Builders<MessageDoc>.Filter.In(m => m.Id, ids),
            Builders<MessageDoc>.Update.Set(m => m.ProcessedAt, DateTime.UtcNow),
            cancellationToken: ct);
    }

    private static string ResolveSubject(string? candidate, List<SubjectDoc> subjects)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var match = subjects.FirstOrDefault(s =>
                string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                s.Aliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                return match.Name;
            }
        }

        return subjects.FirstOrDefault()?.Name ?? "other";
    }

    private static string GuessExtension(string msgType) => msgType switch
    {
        "image" => "jpg",
        "video" => "mp4",
        "audio" => "m4a",
        _ => "bin"
    };

    private static string GuessContentType(string msgType) => msgType switch
    {
        "image" => "image/jpeg",
        "video" => "video/mp4",
        "audio" => "audio/mp4",
        _ => "application/octet-stream"
    };
}
