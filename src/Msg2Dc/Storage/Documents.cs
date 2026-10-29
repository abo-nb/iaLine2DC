using MongoDB.Bson;

namespace Msg2Dc.Storage;

public sealed class GroupDoc
{
    public ObjectId Id { get; set; }
    public string LineGroupId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? SubjectName { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class MessageDoc
{
    public ObjectId Id { get; set; }
    public string WebhookEventId { get; set; } = "";
    public string LineMessageId { get; set; } = "";
    public string LineGroupId { get; set; } = "";
    public string SenderUserId { get; set; } = "";
    public string? SenderName { get; set; }
    public string MsgType { get; set; } = "text";
    public string? Text { get; set; }
    public string RawJson { get; set; } = "";
    public DateTime SentAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public List<AttachmentDoc> Attachments { get; set; } = new();
}

public sealed class AttachmentDoc
{
    public string LineMessageId { get; set; } = "";
    public string Kind { get; set; } = "file";
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long Size { get; set; }
    public string? ArchiveChannelId { get; set; }
    public string? ArchiveMessageId { get; set; }
    public string? ArchiveAttachmentId { get; set; }
    public string? Status { get; set; }
    public DateTime? SavedAt { get; set; }
}

public sealed class ItemDoc
{
    public ObjectId Id { get; set; }
    public ObjectId MessageId { get; set; }
    public string LineMessageId { get; set; } = "";
    public string LineGroupId { get; set; } = "";
    public string SubjectName { get; set; } = "other";
    public string Kind { get; set; } = "chat";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? BodyMd { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime? DeadlineAt { get; set; }
    public List<string> Links { get; set; } = new();
    public double Confidence { get; set; }
    public string Status { get; set; } = "published";
    public List<string> SearchTokens { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PostDoc
{
    public ObjectId Id { get; set; }
    public ObjectId ItemId { get; set; }
    public string LineMessageId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string DiscordMessageId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class SubjectDoc
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class SubscriptionDoc
{
    public ObjectId Id { get; set; }
    public string GuildId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class SenderDoc
{
    public ObjectId Id { get; set; }
    public string LineUserId { get; set; } = "";
    public string LineGroupId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Alias { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class LlmSelection
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class SettingsDoc
{
    public ObjectId Id { get; set; }
    public string? ArchiveChannelId { get; set; }
    public string? DefaultChannelId { get; set; }
    public string? SystemChannelId { get; set; }
    public LlmSelection Llm { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class JobDoc
{
    public const string StatusPending = "pending";
    public const string StatusRunning = "running";
    public const string StatusDone = "done";
    public const string StatusDead = "dead";

    public ObjectId Id { get; set; }
    public string Kind { get; set; } = "process_message";
    public string LineMessageId { get; set; } = "";
    public string Status { get; set; } = StatusPending;
    public int Attempts { get; set; }
    public DateTime RunAfter { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class LlmLogDoc
{
    public ObjectId Id { get; set; }
    public string LineMessageId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public string RawOutput { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
}
