using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Storage;

namespace Msg2Dc.Line;

public sealed class WebhookIngest
{
    private readonly MongoStore _store;
    private readonly JobQueue _queue;
    private readonly PipelineOptions _pipeline;
    private readonly ILogger<WebhookIngest> _log;

    public WebhookIngest(MongoStore store, JobQueue queue, IOptions<PipelineOptions> pipeline, ILogger<WebhookIngest> log)
    {
        _store = store;
        _queue = queue;
        _pipeline = pipeline.Value;
        _log = log;
    }

    public async Task HandleAsync(LineWebhookPayload payload, CancellationToken ct)
    {
        foreach (var ev in payload.Events)
        {
            switch (ev.Type)
            {
                case "message":
                    await OnMessageAsync(ev, ct);
                    break;
                case "edit":
                    await OnEditAsync(ev, ct);
                    break;
                case "unsend":
                    await OnUnsendAsync(ev, ct);
                    break;
                case "join":
                    await OnJoinAsync(ev, ct);
                    break;
                case "leave":
                    await OnLeaveAsync(ev, ct);
                    break;
                default:
                    _log.LogDebug("Ignored LINE event {Type}", ev.Type);
                    break;
            }
        }
    }

    private async Task OnMessageAsync(LineEvent ev, CancellationToken ct)
    {
        var groupId = ev.Source?.GroupId ?? ev.Source?.RoomId;
        if (string.IsNullOrEmpty(groupId) || ev.Message is null)
        {
            _log.LogDebug("Skipped message outside a group chat");
            return;
        }

        await EnsureGroupAsync(groupId, ct);

        var sentAt = ev.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ev.Timestamp).UtcDateTime
            : DateTime.UtcNow;

        var doc = new MessageDoc
        {
            WebhookEventId = ev.WebhookEventId ?? $"{ev.Message.Id}:{sentAt.Ticks}",
            LineMessageId = ev.Message.Id,
            LineGroupId = groupId,
            SenderUserId = ev.Source?.UserId ?? "",
            MsgType = ev.Message.Type,
            Text = ev.Message.Text,
            RawJson = JsonSerializer.Serialize(ev),
            SentAt = sentAt,
            ReceivedAt = DateTime.UtcNow
        };

        try
        {
            await _store.Messages.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
        {
            _log.LogDebug("Duplicate webhook event {EventId} ignored", doc.WebhookEventId);
            return;
        }

        await _queue.EnqueueProcessAsync(doc.LineMessageId, TimeSpan.FromSeconds(_pipeline.CoalesceSeconds), ct);
        _log.LogInformation("Queued message {MessageId} from group {GroupId}", doc.LineMessageId, groupId);
    }

    private async Task OnEditAsync(LineEvent ev, CancellationToken ct)
    {
        if (ev.Message is null)
        {
            return;
        }

        var result = await _store.Messages.UpdateOneAsync(
            Builders<MessageDoc>.Filter.Eq(m => m.LineMessageId, ev.Message.Id),
            Builders<MessageDoc>.Update
                .Set(m => m.Text, ev.Message.Text)
                .Set(m => m.ProcessedAt, null),
            cancellationToken: ct);

        if (result.MatchedCount > 0)
        {
            await _queue.EnqueueProcessAsync(ev.Message.Id, TimeSpan.FromSeconds(2), ct);
            _log.LogInformation("Requeued edited message {MessageId}", ev.Message.Id);
        }
    }

    private async Task OnUnsendAsync(LineEvent ev, CancellationToken ct)
    {
        if (ev.Unsend is null)
        {
            return;
        }

        await _store.Messages.UpdateOneAsync(
            Builders<MessageDoc>.Filter.Eq(m => m.LineMessageId, ev.Unsend.MessageId),
            Builders<MessageDoc>.Update.Set(m => m.DeletedAt, DateTime.UtcNow),
            cancellationToken: ct);

        await _queue.EnqueueDeletePostsAsync(ev.Unsend.MessageId, ct);
        _log.LogInformation("Unsend received for {MessageId}", ev.Unsend.MessageId);
    }

    private async Task OnJoinAsync(LineEvent ev, CancellationToken ct)
    {
        var groupId = ev.Source?.GroupId ?? ev.Source?.RoomId;
        if (string.IsNullOrEmpty(groupId))
        {
            return;
        }

        await EnsureGroupAsync(groupId, ct);
        _log.LogInformation("Bot joined group {GroupId}; bind a subject with /group-bind", groupId);
    }

    private async Task OnLeaveAsync(LineEvent ev, CancellationToken ct)
    {
        var groupId = ev.Source?.GroupId ?? ev.Source?.RoomId;
        if (string.IsNullOrEmpty(groupId))
        {
            return;
        }

        await _store.Groups.UpdateOneAsync(
            Builders<GroupDoc>.Filter.Eq(g => g.LineGroupId, groupId),
            Builders<GroupDoc>.Update.Set(g => g.Active, false).Set(g => g.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

        _log.LogInformation("Bot left group {GroupId}", groupId);
    }

    private Task EnsureGroupAsync(string groupId, CancellationToken ct) =>
        _store.Groups.UpdateOneAsync(
            Builders<GroupDoc>.Filter.Eq(g => g.LineGroupId, groupId),
            Builders<GroupDoc>.Update
                .SetOnInsert(g => g.CreatedAt, DateTime.UtcNow)
                .Set(g => g.UpdatedAt, DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true },
            ct);
}
