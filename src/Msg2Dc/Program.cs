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
using Msg2Dc.Verification;

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
builder.Services.AddSingleton<VerificationService>();

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

// ─── 身份验证 API（给其他服务器用）───────────────────────────────
// 成员名单和发起验证需要 X-Api-Key（管理员用 /verify-key 查看）；
// confirm / token / me 只需要各自拿到的东西，不再要求 API Key。

app.MapGet("/api/verify/status", async (VerificationService verify, CancellationToken ct) =>
{
    var setting = await verify.GetSettingAsync(ct);
    return Results.Ok(new
    {
        configured = setting is not null,
        guildId = setting?.GuildId,
        channelId = setting?.ChannelId,
        codeTtlMinutes = setting?.CodeTtlMinutes,
        accessTokenMinutes = setting?.AccessTokenMinutes,
        refreshTokenDays = setting?.RefreshTokenDays
    });
});

app.MapGet("/api/verify/members", async (
    HttpRequest request,
    VerificationService verify,
    CancellationToken ct) =>
{
    if (!await verify.IsApiKeyValidAsync(request.Headers["X-Api-Key"], ct))
    {
        return Results.Json(new { error = "invalid api key" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        var (setting, members) = await verify.ListMembersAsync(ct);
        return Results.Ok(new
        {
            guildId = setting.GuildId,
            channelId = setting.ChannelId,
            count = members.Count,
            members
        });
    }
    catch (VerificationException ex)
    {
        return VerifyError(ex);
    }
});

app.MapPost("/api/verify/request", async (
    HttpRequest request,
    VerificationService verify,
    CancellationToken ct) =>
{
    if (!await verify.IsApiKeyValidAsync(request.Headers["X-Api-Key"], ct))
    {
        return Results.Json(new { error = "invalid api key" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var body = await request.ReadFromJsonAsync<VerifyRequestBody>(jsonOptions, ct);
    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
    {
        return Results.BadRequest(new { error = "userId 不能为空" });
    }

    try
    {
        return Results.Ok(await verify.CreateRequestAsync(body.UserId, ct));
    }
    catch (VerificationException ex)
    {
        return VerifyError(ex);
    }
});

app.MapPost("/api/verify/confirm", async (
    HttpRequest request,
    VerificationService verify,
    CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<VerifyConfirmBody>(jsonOptions, ct);
    if (body is null)
    {
        return Results.BadRequest(new { error = "请求体不能为空" });
    }

    try
    {
        return Results.Ok(await verify.ConfirmAsync(body.RequestId ?? "", body.Code ?? "", ct));
    }
    catch (VerificationException ex)
    {
        return VerifyError(ex);
    }
});

app.MapPost("/api/verify/token", async (
    HttpRequest request,
    VerificationService verify,
    CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<VerifyRefreshBody>(jsonOptions, ct);
    if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
    {
        return Results.BadRequest(new { error = "refreshToken 不能为空" });
    }

    try
    {
        return Results.Ok(await verify.RefreshAsync(body.RefreshToken, ct));
    }
    catch (VerificationException ex)
    {
        return VerifyError(ex);
    }
});

app.MapGet("/api/verify/me", async (
    HttpRequest request,
    VerificationService verify,
    CancellationToken ct) =>
{
    var header = request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..].Trim()
        : "";

    try
    {
        return Results.Ok(await verify.GetUserAsync(token, ct));
    }
    catch (VerificationException ex)
    {
        return VerifyError(ex);
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

static IResult VerifyError(VerificationException ex) =>
    Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);

app.Run();

public sealed record VerifyRequestBody(string UserId);

public sealed record VerifyConfirmBody(string? RequestId, string? Code);

public sealed record VerifyRefreshBody(string? RefreshToken);
