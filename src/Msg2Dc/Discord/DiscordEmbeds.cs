using Discord;
using Msg2Dc.Storage;

namespace Msg2Dc.Discord;

public static class DiscordEmbeds
{
    private const int TitleMax = 256;
    private const int DescriptionMax = 4096;
    private const int FieldValueMax = 1024;
    private const int FooterMax = 2048;
    private const int TotalMax = 6000;
    private const int MaxFields = 25;

    public static Embed ItemCard(ItemDoc item, string? senderName, IReadOnlyList<ArchivedAttachment> attachments)
    {
        var builder = new EmbedBuilder().WithColor(ColorFor(item.Kind));

        var title = DiscordText.Clip(item.Title, TitleMax);
        if (title.Length > 0)
        {
            builder.WithTitle(title);
        }

        var fields = new List<(string Name, string Value)>();
        AddField(fields, "科目", item.SubjectName);
        AddField(fields, "类型", KindLabel(item.Kind));
        AddField(fields, "发送者", senderName);

        if (item.DeadlineAt is { } deadline)
        {
            AddField(fields, "截止", $"{DiscordText.Relative(deadline)}（{DiscordText.Absolute(deadline)}）");
        }

        if (item.Tags is { Count: > 0 })
        {
            AddField(fields, "标签", string.Join(" · ", item.Tags));
        }

        AddAttachmentField(fields, attachments);

        var footer = DiscordText.Clip($"LINE {item.LineMessageId}", FooterMax);
        var used = title.Length + footer.Length + fields.Sum(f => f.Name.Length + f.Value.Length);
        var descriptionBudget = Math.Clamp(TotalMax - used, 0, DescriptionMax);
        var description = BuildDescription(item, descriptionBudget);
        if (description.Length > 0)
        {
            builder.WithDescription(description);
        }

        foreach (var field in fields.Take(MaxFields))
        {
            builder.AddField(field.Name, field.Value);
        }

        builder.WithFooter(footer);
        return builder.Build();
    }

    private static string BuildDescription(ItemDoc item, int budget)
    {
        if (budget <= 0)
        {
            return "";
        }

        var parts = new List<string>();
        var remaining = budget;

        var summary = DiscordText.Clip(item.Summary, remaining);
        if (summary.Length > 0)
        {
            parts.Add(summary);
            remaining -= summary.Length + 2;
        }

        if (remaining > 8)
        {
            var body = DiscordText.Clip(item.BodyMd, remaining);
            if (body.Length > 0)
            {
                parts.Add(body);
            }
        }

        return string.Join("\n\n", parts);
    }

    private static void AddAttachmentField(List<(string Name, string Value)> fields, IReadOnlyList<ArchivedAttachment> attachments)
    {
        var archived = attachments.Where(a => a.Status == "archived").Take(5).ToList();
        if (archived.Count == 0)
        {
            return;
        }

        var lines = archived.Select(a =>
        {
            var name = string.IsNullOrWhiteSpace(a.FileName) ? a.Kind : a.FileName!;
            return ulong.TryParse(a.ArchiveChannelId, out var channelId) && ulong.TryParse(a.ArchiveMessageId, out var messageId)
                ? $"[{name}](https://discord.com/channels/{channelId}/{messageId})"
                : name;
        });

        AddField(fields, "附件", string.Join("\n", lines));
    }

    private static void AddField(List<(string Name, string Value)> fields, string name, string? value)
    {
        var text = DiscordText.Clip(value, FieldValueMax);
        if (text.Length > 0)
        {
            fields.Add((name, text));
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "task" or "assignment" => "任务",
        "exam" or "test" => "考试",
        "notice" or "announcement" => "通知",
        "material" or "resource" or "file" => "资料",
        "chat" => "聊天",
        _ => string.IsNullOrWhiteSpace(kind) ? "其他" : kind
    };

    private static Color ColorFor(string kind) => kind switch
    {
        "task" or "assignment" => new Color(230, 126, 34),
        "exam" or "test" => new Color(231, 76, 60),
        "notice" or "announcement" => new Color(46, 204, 113),
        "material" or "resource" or "file" => new Color(52, 152, 219),
        "chat" => new Color(149, 165, 166),
        _ => new Color(155, 89, 182)
    };
}
