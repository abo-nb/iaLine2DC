namespace Msg2Dc.Line;

public sealed class LineWebhookPayload
{
    public string? Destination { get; set; }
    public List<LineEvent> Events { get; set; } = new();
}

public sealed class LineEvent
{
    public string Type { get; set; } = "";
    public string? WebhookEventId { get; set; }
    public long Timestamp { get; set; }
    public string? ReplyToken { get; set; }
    public string? Mode { get; set; }
    public LineSource? Source { get; set; }
    public LineMessage? Message { get; set; }
    public LineUnsend? Unsend { get; set; }
    public LineDeliveryContext? DeliveryContext { get; set; }
}

public sealed class LineSource
{
    public string Type { get; set; } = "";
    public string? UserId { get; set; }
    public string? GroupId { get; set; }
    public string? RoomId { get; set; }
}

public sealed class LineMessage
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public string? StickerId { get; set; }
    public string? PackageId { get; set; }
    public long? Duration { get; set; }
}

public sealed class LineUnsend
{
    public string MessageId { get; set; } = "";
}

public sealed class LineDeliveryContext
{
    public bool IsRedelivery { get; set; }
}
