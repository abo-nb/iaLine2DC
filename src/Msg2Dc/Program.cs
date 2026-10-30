using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Msg2Dc;
using Msg2Dc.Configuration;
using Msg2Dc.Discord;
using Msg2Dc.Line;
using Msg2Dc.Llm;
using Msg2Dc.Pipeline;
using Msg2Dc.Storage;

var builder = WebApplication.CreateBuilder(args);

DotEnv.Load(Path.Combine(builder.Environment.ContentRootPath, ".env"), builder.Configuration);

builder.Services.Configure<LineOptions>(builder.Configuration.GetSection("Line"));
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.AddSingleton(RuntimeOptions.FromConfiguration(builder.Configuration));

builder.Services.AddSingleton<MongoStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<WebhookIngest>();
builder.Services.AddSingleton<LineApiClient>();

builder.Services.AddLlmModule();
builder.Services.AddDiscordModule();

builder.Services.AddHostedService<MongoInitializer>();
builder.Services.AddHostedService<MessageWorker>();
builder.Services.AddHostedService<KeepAliveService>();

var app = builder.Build();

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    app.Urls.Add($"http://0.0.0.0:{port}");
}

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

app.MapGet("/healthz", async (
    MongoStore store,
    JobQueue queue,
    IOptions<LineOptions> line,
    ILlmClassifier classifier,
    DiscordSocketClient discordClient,
    DiscordRateLimitGuard rateLimit,
    CancellationToken ct) =>
{
    var mongoUp = await store.PingAsync(TimeSpan.FromSeconds(3), ct);
    long pending = 0;
    if (mongoUp)
    {
        try
        {
            pending = await queue.PendingCountAsync(ct);
        }
        catch
        {
            pending = -1;
        }
    }

    return Results.Json(new
    {
        status = mongoUp ? "ok" : "degraded",
        mongo = mongoUp,
        queuePending = pending,
        lineChannelId = line.Value.ChannelId,
        llm = classifier.DisplayName,
        discord = new
        {
            state = discordClient.ConnectionState.ToString(),
            rateLimitedUntil = rateLimit.IsBlocked ? rateLimit.BlockedUntil : (DateTimeOffset?)null,
            lastRetryAfterSeconds = rateLimit.LastRetryAfterSeconds
        },
        utc = DateTime.UtcNow
    });
});

app.MapPost("/line/webhook", async (
    HttpRequest request,
    WebhookIngest ingest,
    IOptions<LineOptions> line,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("LineWebhook");

    string body;
    using (var reader = new StreamReader(request.Body))
    {
        body = await reader.ReadToEndAsync(ct);
    }

    var signature = request.Headers["X-Line-Signature"].ToString();
    if (!LineSignature.Verify(line.Value.ChannelSecret, body, signature))
    {
        log.LogWarning("Rejected webhook with invalid signature");
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    LineWebhookPayload? payload;
    try
    {
        payload = JsonSerializer.Deserialize<LineWebhookPayload>(body, jsonOptions);
    }
    catch (JsonException ex)
    {
        log.LogWarning(ex, "Malformed webhook body");
        return Results.BadRequest();
    }

    if (payload is null)
    {
        return Results.BadRequest();
    }

    try
    {
        await ingest.HandleAsync(payload, ct);
        return Results.Ok(new { ok = true, events = payload.Events.Count });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Webhook ingest failed; returning 500 so LINE redelivers");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/", () => Results.Redirect("/healthz"));

app.MapGet("/diag/discord", async (CancellationToken ct) =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    string egressIp;
    try
    {
        egressIp = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
    }
    catch (Exception ex)
    {
        egressIp = $"error: {ex.Message}";
    }

    var probes = new Dictionary<string, object>();
    foreach (var url in new[] { "https://discord.com/api/v10/gateway", "https://discord.com/api/v10/users/@me" })
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            probes[url] = new
            {
                status = (int)response.StatusCode,
                contentType = response.Content.Headers.ContentType?.ToString(),
                retryAfter = response.Headers.RetryAfter?.ToString(),
                server = response.Headers.TryGetValues("server", out var server) ? string.Join(",", server) : null,
                body = body.Length > 160 ? body[..160] : body
            };
        }
        catch (Exception ex)
        {
            probes[url] = ex.Message;
        }
    }

    return Results.Json(new { egressIp, probes });
});

app.Run();
