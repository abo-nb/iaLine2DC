using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Msg2Dc.Llm;

public sealed class GeminiChatCompletionService : IChatCompletionService
{
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ILogger _log;
    private readonly IReadOnlyDictionary<string, object?> _attributes;

    public GeminiChatCompletionService(
        string model,
        string baseUrl,
        string apiKey,
        HttpClient http,
        int timeoutSeconds,
        ILogger log)
    {
        _model = model;
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _http = http;
        _timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
        _log = log;
        _attributes = new Dictionary<string, object?>
        {
            ["ModelId"] = model,
            ["Endpoint"] = baseUrl
        };
    }

    public IReadOnlyDictionary<string, object?> Attributes => _attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);

        var payload = BuildPayload(chatHistory, executionSettings);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl());
        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Gemini request failed with {Status}: {Body}", (int)response.StatusCode, Truncate(body, 1000));
            throw new HttpRequestException($"Gemini 接口返回 HTTP {(int)response.StatusCode}：{Truncate(body, 2000)}");
        }

        var text = ExtractText(body);
        return new[] { new ChatMessageContent(AuthorRole.Assistant, text) };
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Gemini streaming is not supported.");

    private string BuildUrl()
    {
        var model = _model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? _model["models/".Length..]
            : _model;
        return $"{_baseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
    }

    private static JsonObject BuildPayload(ChatHistory chatHistory, PromptExecutionSettings? executionSettings)
    {
        var systemParts = new JsonArray();
        var contents = new JsonArray();
        string? lastRole = null;
        JsonArray? lastParts = null;

        foreach (var message in chatHistory)
        {
            var text = message.Content;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (message.Role == AuthorRole.System)
            {
                systemParts.Add(new JsonObject { ["text"] = text });
                continue;
            }

            var role = message.Role == AuthorRole.Assistant ? "model" : "user";
            if (role == lastRole && lastParts is not null)
            {
                lastParts.Add(new JsonObject { ["text"] = text });
                continue;
            }

            lastParts = new JsonArray(new JsonObject { ["text"] = text });
            contents.Add(new JsonObject
            {
                ["role"] = role,
                ["parts"] = lastParts
            });
            lastRole = role;
        }

        var generationConfig = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["temperature"] = 0.2
        };

        var maxTokens = GetMaxTokens(executionSettings);
        if (maxTokens is > 0)
        {
            generationConfig["maxOutputTokens"] = maxTokens.Value;
        }

        var payload = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig
        };

        if (systemParts.Count > 0)
        {
            payload["systemInstruction"] = new JsonObject { ["parts"] = systemParts };
        }

        return payload;
    }

    private static int? GetMaxTokens(PromptExecutionSettings? settings)
    {
        if (settings?.ExtensionData is { } data)
        {
            if (data.TryGetValue("max_tokens", out var raw) && TryToInt(raw, out var maxTokens))
            {
                return maxTokens;
            }

            if (data.TryGetValue("max_output_tokens", out var alternative) && TryToInt(alternative, out var alternativeTokens))
            {
                return alternativeTokens;
            }
        }

        return settings is OpenAIPromptExecutionSettings openAi && openAi.MaxTokens is int value && value > 0
            ? value
            : null;
    }

    private static bool TryToInt(object? value, out int result)
    {
        switch (value)
        {
            case int number:
                result = number;
                return true;
            case long number when number >= int.MinValue && number <= int.MaxValue:
                result = (int)number;
                return true;
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var elementValue))
                {
                    result = elementValue;
                    return true;
                }

                break;
            case string text:
                if (int.TryParse(text, out var stringValue))
                {
                    result = stringValue;
                    return true;
                }

                break;
        }

        result = 0;
        return false;
    }

    private static string ExtractText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var blockReason) &&
                blockReason.ValueKind == JsonValueKind.String)
            {
                throw new InvalidOperationException($"Gemini 拒绝了该请求，blockReason={blockReason.GetString()}");
            }

            if (!root.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content) ||
                    !content.TryGetProperty("parts", out var parts) ||
                    parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }

                break;
            }

            return builder.ToString();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Gemini 响应无法解析：{Truncate(body, 500)}", ex);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
