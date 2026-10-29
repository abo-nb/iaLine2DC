using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Msg2Dc.Configuration;

namespace Msg2Dc.Line;

public sealed class LineApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
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

    public async Task<byte[]?> DownloadContentAsync(string messageId, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
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

    public async Task<string?> GetGroupMemberNameAsync(string groupId, string userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(userId))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(
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
