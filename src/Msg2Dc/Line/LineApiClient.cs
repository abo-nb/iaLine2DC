using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Msg2Dc.Configuration;

namespace Msg2Dc.Line;

public sealed class LineApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // This bot only receives. Keep the LINE side read-only: no push, reply, multicast,
    // broadcast or narrowcast request may ever leave this process.
    private static readonly string[] ForbiddenLinePaths =
    {
        "/message/push",
        "/message/reply",
        "/message/multicast",
        "/message/broadcast",
        "/message/narrowcast"
    };

    private readonly HttpClient _http;
    private readonly LineOptions _line;
    private readonly ILogger<LineApiClient> _log;

    public LineApiClient(IOptions<LineOptions> line, ILogger<LineApiClient> log)
    {
        _line = line.Value;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrEmpty(_line.ChannelAccessToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _line.ChannelAccessToken);
        }
    }

    private const long MaxContentBytes = 20_000_000;

    /// <summary>
    /// Every LINE call goes through here. It is a hard block, not a convention: if a future
    /// change tries to call a send endpoint, it fails loudly instead of messaging the class.
    /// </summary>
    private Task<HttpResponseMessage> ReadAsync(string url, CancellationToken ct)
    {
        foreach (var forbidden in ForbiddenLinePaths)
        {
            if (url.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Blocked LINE send endpoint '{forbidden}': this bot is receive-only.");
            }
        }

        return _http.GetAsync(url, ct);
    }


    public async Task<byte[]?> DownloadContentAsync(string messageId, CancellationToken ct)
    {
        try
        {
            using var response = await ReadAsync(
                $"https://api-data.line.me/v2/bot/message/{Uri.EscapeDataString(messageId)}/content", ct);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status is 404 or 410)
                {
                    _log.LogWarning("Content {MessageId} is gone ({Status})", messageId, status);
                    return null;
                }

                throw new HttpRequestException($"LINE content endpoint returned {status} for {messageId}");
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxContentBytes)
            {
                _log.LogWarning("Content {MessageId} is {Bytes} bytes, above the {Limit} byte cap",
                    messageId, length, MaxContentBytes);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxContentBytes)
            {
                _log.LogWarning("Content {MessageId} exceeded the {Limit} byte cap after download",
                    messageId, MaxContentBytes);
                return null;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Content download failed for {messageId}", ex);
        }
    }

    /// <summary>
    /// Group chat id alone is unreadable, so pull the group name once and remember it.
    /// </summary>
    public async Task<string?> GetGroupNameAsync(string groupId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            return null;
        }

        try
        {
            using var response = await ReadAsync(
                $"https://api.line.me/v2/bot/group/{Uri.EscapeDataString(groupId)}/summary", ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("groupName", out var name) ? name.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Group summary lookup failed for {GroupId}", groupId);
            return null;
        }
    }

    public async Task<string?> GetGroupMemberNameAsync(string groupId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(userId))
        {
            return null;
        }

        try
        {
            using var response = await ReadAsync(
                $"https://api.line.me/v2/bot/group/{Uri.EscapeDataString(groupId)}/member/{Uri.EscapeDataString(userId)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Profile lookup failed for {UserId} in {GroupId}", userId, groupId);
            return null;
        }
    }
}
