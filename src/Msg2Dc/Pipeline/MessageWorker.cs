using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Storage;

namespace Msg2Dc.Pipeline;

public sealed class MessageWorker : BackgroundService
{
    private const int MaxBatchSize = 10;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StaleSweepInterval = TimeSpan.FromMinutes(2);

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

        _ = Task.Run(() => SweepStaleJobsAsync(stoppingToken), stoppingToken);

        var loops = Enumerable.Range(0, Math.Max(1, _pipeline.WorkerCount))
            .Select(_ => Task.Run(() => LoopAsync(stoppingToken), stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task SweepStaleJobsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queue.RequeueStaleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to requeue stale jobs");
            }

            try
            {
                await Task.Delay(StaleSweepInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
                await DelaySafeAsync(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            if (job is null)
            {
                await DelaySafeAsync(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            try
            {
                await ProcessAsync(job, ct);
                await _queue.CompleteAsync(job.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job {JobId} ({Kind}) failed", job.Id, job.Kind);
                try
                {
                    await _queue.FailAsync(job, ex.Message, CancellationToken.None);
                }
                catch (Exception failEx)
                {
                    _log.LogError(failEx, "Failed to record job failure for {JobId}", job.Id);
                }
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

        var batch = await ClaimBatchAsync(message, ct);
        if (batch.Count == 0)
        {
            _log.LogDebug("Message {MessageId} was already claimed by another worker", message.LineMessageId);
            return;
        }

        try
        {
            var text = string.Join("\n", batch
                .Select(m => m.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim()));

            var attachments = await ArchiveAttachmentsAsync(batch, ct);

            if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            {
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

            var withdrawn = await IsWithdrawnAsync(message.Id, ct);
            if (withdrawn)
            {
                _log.LogInformation("Message {MessageId} was withdrawn while processing; skipping publish", message.LineMessageId);
                await _queue.EnqueueDeletePostsAsync(message.LineMessageId, ct);
                return;
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

                var channelIds = await ResolveChannelsAsync(item, ct);
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

            _log.LogInformation("Processed {MessageId}: {Count} item(s)", message.LineMessageId, items.Count);
        }
        catch
        {
            await ReleaseBatchAsync(batch, CancellationToken.None);
            throw;
        }
    }

    private async Task<List<MessageDoc>> ClaimBatchAsync(MessageDoc seed, CancellationToken ct)
    {
        var cutoff = seed.SentAt.AddSeconds(-BatchWindow.TotalSeconds);
        var filter = Builders<MessageDoc>.Filter.And(
            Builders<MessageDoc>.Filter.Eq(m => m.LineGroupId, seed.LineGroupId),
            Builders<MessageDoc>.Filter.Eq(m => m.SenderUserId, seed.SenderUserId),
            Builders<MessageDoc>.Filter.Eq(m => m.ProcessedAt, null),
            Builders<MessageDoc>.Filter.Eq(m => m.DeletedAt, null),
            Builders<MessageDoc>.Filter.Gte(m => m.SentAt, cutoff),
            Builders<MessageDoc>.Filter.Lte(m => m.SentAt, DateTime.UtcNow));

        var candidates = await _store.Messages
            .Find(filter)
            .SortBy(m => m.SentAt)
            .Limit(MaxBatchSize)
            .ToListAsync(ct);

        var claimed = new List<MessageDoc>();
        foreach (var candidate in candidates)
        {
            var claim = Builders<MessageDoc>.Filter.And(
                Builders<MessageDoc>.Filter.Eq(m => m.Id, candidate.Id),
                Builders<MessageDoc>.Filter.Eq(m => m.ProcessedAt, null));

            var result = await _store.Messages.FindOneAndUpdateAsync(
                claim,
                Builders<MessageDoc>.Update.Set(m => m.ProcessedAt, DateTime.UtcNow),
                new FindOneAndUpdateOptions<MessageDoc> { ReturnDocument = ReturnDocument.After },
                ct);

            if (result is not null)
            {
                claimed.Add(result);
            }
        }

        return claimed;
    }

    private Task MarkProcessedAsync(List<MessageDoc> batch, CancellationToken ct) =>
        UpdateProcessedAtAsync(batch, DateTime.UtcNow, ct);

    private Task ReleaseBatchAsync(List<MessageDoc> batch, CancellationToken ct) =>
        UpdateProcessedAtAsync(batch, null, ct);

    private Task UpdateProcessedAtAsync(List<MessageDoc> batch, DateTime? value, CancellationToken ct)
    {
        var ids = batch.Select(m => m.Id).ToList();
        return _store.Messages.UpdateManyAsync(
            Builders<MessageDoc>.Filter.In(m => m.Id, ids),
            Builders<MessageDoc>.Update.Set(m => m.ProcessedAt, value),
            cancellationToken: ct);
    }

    private async Task<bool> IsWithdrawnAsync(MongoDB.Bson.ObjectId messageId, CancellationToken ct)
    {
        var current = await _store.Messages
            .Find(Builders<MessageDoc>.Filter.Eq(m => m.Id, messageId))
            .Project(m => m.DeletedAt)
            .FirstOrDefaultAsync(ct);

        return current is not null;
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
            var record = stored ?? new ArchivedAttachment(message.LineMessageId, message.MsgType, fileName,
                GuessContentType(message.MsgType), content.Length, null, null, null, "media_lost");
            archived.Add(record);

            await _store.Messages.UpdateOneAsync(
                Builders<MessageDoc>.Filter.Eq(m => m.Id, message.Id),
                Builders<MessageDoc>.Update.Set(m => m.Attachments, new List<AttachmentDoc>
                {
                    new()
                    {
                        LineMessageId = record.LineMessageId,
                        Kind = record.Kind,
                        FileName = record.FileName,
                        ContentType = record.ContentType,
                        Size = record.Size,
                        ArchiveChannelId = record.ArchiveChannelId,
                        ArchiveMessageId = record.ArchiveMessageId,
                        ArchiveAttachmentId = record.ArchiveAttachmentId,
                        Status = record.Status,
                        SavedAt = DateTime.UtcNow
                    }
                }),
                cancellationToken: ct);
        }

        return archived;
    }

    private async Task<List<string>> ResolveChannelsAsync(ItemDoc item, CancellationToken ct)
    {
        var subscriptions = await _store.Subscriptions
            .Find(Builders<SubscriptionDoc>.Filter.Eq(s => s.SubjectName, item.SubjectName))
            .ToListAsync(ct);

        var channels = subscriptions.Select(s => s.ChannelId).Distinct().ToList();
        if (channels.Count > 0)
        {
            return channels;
        }

        var settings = await _store.Settings.Find(FilterDefinition<SettingsDoc>.Empty).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(settings?.DefaultChannelId))
        {
            return new List<string> { settings.DefaultChannelId };
        }

        return new List<string>();
    }

    private static string ResolveSubject(string? candidate, List<SubjectDoc> subjects)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "other";
        }

        var match = subjects.FirstOrDefault(s =>
            string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
            s.Aliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)));

        return match?.Name ?? "other";
    }

    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
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
