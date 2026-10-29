using Msg2Dc.Storage;

namespace Msg2Dc;

public sealed record SubjectInfo(string Name, IReadOnlyList<string> Aliases);

public sealed record ClassifiedItem(
    string SubjectName,
    string Kind,
    string Title,
    string Summary,
    string? BodyMd,
    IReadOnlyList<string> Tags,
    DateTime? DeadlineAt,
    IReadOnlyList<string> Links,
    double Confidence);

public sealed record ClassifyRequest(
    string? SubjectHint,
    string SenderName,
    DateTime SentAt,
    string Text,
    IReadOnlyList<SubjectInfo> Subjects,
    IReadOnlyList<string> AttachmentNames,
    string LineMessageId);

public sealed record ClassifyResult(bool Ok, IReadOnlyList<ClassifiedItem> Items, string? Error)
{
    public static ClassifyResult Empty { get; } = new(true, Array.Empty<ClassifiedItem>(), null);
    public static ClassifyResult Failed(string error) => new(false, Array.Empty<ClassifiedItem>(), error);
}

public interface ILlmClassifier
{
    string DisplayName { get; }

    Task<ClassifyResult> ClassifyAsync(ClassifyRequest request, CancellationToken ct);
}

public interface ILlmModelSwitcher
{
    (string Provider, string Model) Current { get; }

    Task<(bool Ok, string Message)> SetModelAsync(string provider, string model, CancellationToken ct);
}

public sealed record ArchivedAttachment(
    string LineMessageId,
    string Kind,
    string? FileName,
    string? ContentType,
    long Size,
    string? ArchiveChannelId,
    string? ArchiveMessageId,
    string? ArchiveAttachmentId,
    string Status);

public sealed record PostRef(string ChannelId, string MessageId);

public interface IDiscordPublisher
{
    bool IsReady { get; }

    Task<ArchivedAttachment?> ArchiveAsync(
        string lineMessageId,
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken ct);

    Task<IReadOnlyList<PostRef>> PublishAsync(
        ItemDoc item,
        IReadOnlyList<string> channelIds,
        IReadOnlyList<ArchivedAttachment> attachments,
        CancellationToken ct);

    Task DeleteByLineMessageIdAsync(string lineMessageId, CancellationToken ct);
}
