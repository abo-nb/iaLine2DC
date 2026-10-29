namespace Msg2Dc.Configuration;

public sealed class LineOptions
{
    public string ChannelId { get; set; } = "";
    public string ChannelSecret { get; set; } = "";
    public string ChannelAccessToken { get; set; } = "";
}

public sealed class DiscordOptions
{
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public string GuildId { get; set; } = "";
    public int CooldownMinutes { get; set; } = 10;
}

public sealed class OpenAiOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
}

public sealed class GoogleOptions
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    public string ApiKey { get; set; } = "";
}

public sealed class LlmOptions
{
    public OpenAiOptions OpenAi { get; set; } = new();
    public GoogleOptions Google { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 90;
}

public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string Database { get; set; } = "msg2dc";
}

public sealed class PipelineOptions
{
    public int CoalesceSeconds { get; set; } = 30;
    public double MinConfidence { get; set; } = 0.6;
    public int WorkerCount { get; set; } = 2;
    public string DataDir { get; set; } = "./data";
}

public sealed class RuntimeOptions
{
    public IReadOnlyList<ulong> AdminIds { get; init; } = Array.Empty<ulong>();

    public static RuntimeOptions FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration["AdminIds"] ?? "";
        var ids = raw
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ulong.TryParse(value, out var id) ? id : 0UL)
            .Where(id => id != 0)
            .ToArray();
        return new RuntimeOptions { AdminIds = ids };
    }

    public bool IsAdmin(ulong userId) => AdminIds.Contains(userId);
}
