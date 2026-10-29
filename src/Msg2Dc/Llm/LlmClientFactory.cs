using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Msg2Dc.Configuration;

namespace Msg2Dc.Llm;

public sealed class LlmClientFactory
{
    public const string OpenAiProvider = "openai";
    public const string GeminiProvider = "gemini";

    private readonly ConcurrentDictionary<string, IChatCompletionService> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LlmOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _http;

    public LlmClientFactory(IOptions<LlmOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)) };
    }

    public IChatCompletionService Get(string provider, string model)
    {
        var normalizedProvider = NormalizeProvider(provider)
            ?? throw new ArgumentException($"Unsupported LLM provider: {provider}", nameof(provider));

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty", nameof(model));
        }

        var normalizedModel = model.Trim();
        var key = $"{normalizedProvider}|{normalizedModel}|{BaseUrlOf(normalizedProvider)}";
        return _cache.GetOrAdd(key, _ => Create(normalizedProvider, normalizedModel));
    }

    public void Invalidate() => _cache.Clear();

    public static bool IsSupportedProvider(string? provider) => NormalizeProvider(provider) is not null;

    public static string? NormalizeProvider(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        OpenAiProvider => OpenAiProvider,
        GeminiProvider => GeminiProvider,
        _ => null
    };

    private IChatCompletionService Create(string provider, string model)
    {
        if (provider == OpenAiProvider)
        {
            var options = _options.OpenAi;
#pragma warning disable SKEXP0010
            return new OpenAIChatCompletionService(
                model,
                new Uri(options.BaseUrl, UriKind.Absolute),
                options.ApiKey,
                null,
                _http,
                _loggerFactory);
#pragma warning restore SKEXP0010
        }

        return new GeminiChatCompletionService(
            model,
            _options.Google.BaseUrl,
            _options.Google.ApiKey,
            _http,
            Math.Max(5, _options.TimeoutSeconds),
            _loggerFactory.CreateLogger<GeminiChatCompletionService>());
    }

    private string BaseUrlOf(string provider) =>
        provider == OpenAiProvider ? _options.OpenAi.BaseUrl : _options.Google.BaseUrl;
}
