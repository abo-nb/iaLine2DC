using Discord;

namespace Msg2Dc.Discord;

public static class DiscordCommands
{
    public static readonly HashSet<string> PublicCommands = new(StringComparer.Ordinal)
    {
        "search", "latest", "stats"
    };

    public static readonly HashSet<string> AdminCommands = new(StringComparer.Ordinal)
    {
        "subscribe", "unsubscribe", "subscriptions",
        "subject-add", "subject-remove", "subject-alias", "subjects",
        "set-archive", "set-default",
        "groups", "group-bind",
        "set-model", "reprocess", "status"
    };

    public static IReadOnlyList<(string Name, ApplicationCommandProperties Properties)> Build()
    {
        var definitions = new List<(string Name, ApplicationCommandProperties Properties)>
        {
            ("search", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("search")
                .WithDescription("按关键词搜索已收集的内容")
                .AddOption(Text("query", "搜索关键词", required: true))
                .AddOption(Text("subject", "限定科目（可选）"))
                .AddOption(Integer("days", "只搜索最近 N 天产生的条目（可选）", min: 1, max: 3650))
                .Build()),

            ("latest", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("latest")
                .WithDescription("查看某个科目最新的内容")
                .AddOption(Text("subject", "科目名称或别名", required: true))
                .AddOption(Integer("limit", "返回条数（默认 10，最多 20）", min: 1, max: 20))
                .Build()),

            ("stats", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("stats")
                .WithDescription("查看收集与队列统计")
                .Build()),

            ("subscribe", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("subscribe")
                .WithDescription("将当前频道订阅到某个科目")
                .AddOption(Text("subject", "科目名称或别名（不存在会自动创建）", required: true))
                .Build()),

            ("unsubscribe", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("unsubscribe")
                .WithDescription("取消当前频道对某个科目的订阅")
                .AddOption(Text("subject", "科目名称或别名", required: true))
                .Build()),

            ("subscriptions", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("subscriptions")
                .WithDescription("查看当前频道订阅的科目")
                .Build()),

            ("subject-add", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("subject-add")
                .WithDescription("新增科目")
                .AddOption(Text("name", "科目名称", required: true))
                .AddOption(Text("aliases", "别名，用空格或逗号分隔（可选）"))
                .Build()),

            ("subject-remove", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("subject-remove")
                .WithDescription("删除科目及其订阅")
                .AddOption(Text("name", "科目名称或别名", required: true))
                .Build()),

            ("subject-alias", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("subject-alias")
                .WithDescription("为科目追加别名")
                .AddOption(Text("name", "科目名称", required: true))
                .AddOption(Text("aliases", "别名，用空格或逗号分隔", required: true))
                .Build()),

            ("subjects", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("subjects")
                .WithDescription("列出所有科目")
                .Build()),

            ("set-archive", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("set-archive")
                .WithDescription("设置附件归档频道")
                .AddOption(Channel("channel", "归档频道"))
                .Build()),

            ("set-default", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("set-default")
                .WithDescription("设置没有订阅科目时的默认推送频道")
                .AddOption(Channel("channel", "默认推送频道"))
                .Build()),

            ("groups", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("groups")
                .WithDescription("列出已知的 LINE 群及其绑定科目")
                .Build()),

            ("group-bind", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("group-bind")
                .WithDescription("将 LINE 群绑定到某个科目")
                .AddOption(Text("group", "LINE 群 ID", required: true))
                .AddOption(Text("subject", "科目名称或别名", required: true))
                .Build()),

            ("set-model", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("set-model")
                .WithDescription("切换 LLM 提供方与模型")
                .AddOption(Text("provider", "提供方，例如 openai 或 gemini", required: true))
                .AddOption(Text("model", "模型名称", required: true))
                .Build()),

            ("reprocess", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("reprocess")
                .WithDescription("重新处理某条 LINE 消息")
                .AddOption(Text("message_id", "LINE 消息 ID", required: true))
                .Build()),

            ("status", (ApplicationCommandProperties)new SlashCommandBuilder()
                .WithName("status")
                .WithDescription("查看运行状态")
                .Build())
        };

        return definitions;
    }

    private static SlashCommandOptionBuilder Text(string name, string description, bool required = false) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(required);

    private static SlashCommandOptionBuilder Integer(string name, string description, int min, int max) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Integer)
            .WithMinValue(min)
            .WithMaxValue(max);

    private static SlashCommandOptionBuilder Channel(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Channel)
            .WithRequired(true)
            .AddChannelType(ChannelType.Text);
}
