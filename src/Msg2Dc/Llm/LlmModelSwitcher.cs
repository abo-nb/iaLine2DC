using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MongoDB.Driver;
using Msg2Dc.Configuration;
using Msg2Dc.Storage;

namespace Msg2Dc.Llm;

public sealed class LlmModelSwitcher : ILlmModelSwitcher
{
    private readonly MongoStore _store;
    private readonly LlmClientFactory _factory;
    private readonly LlmSelectionProvider _selection;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmModelSwitcher> _log;

    public LlmModelSwitcher(
        MongoStore store,
        LlmClientFactory factory,
        LlmSelectionProvider selection,
        IOptions<LlmOptions> options,
        ILogger<LlmModelSwitcher> log)
    {
        _store = store;
        _factory = factory;
        _selection = selection;
        _options = options.Value;
        _log = log;
    }

    public (string Provider, string Model) Current
    {
        get
        {
            var selection = _selection.Current ?? _selection.LoadBlocking();
            return selection is null
                ? ("", "")
                : ((selection.Provider ?? "").Trim(), (selection.Model ?? "").Trim());
        }
    }

    public async Task<(bool Ok, string Message)> SetModelAsync(string provider, string model, CancellationToken ct)
    {
        var normalizedProvider = LlmClientFactory.NormalizeProvider(provider);
        if (normalizedProvider is null)
        {
            return (false, $"不支持的模型提供方“{provider}”，目前只支持 openai 和 gemini。");
        }

        var normalizedModel = model?.Trim() ?? "";
        if (normalizedModel.Length == 0)
        {
            return (false, "模型名称不能为空。");
        }

        IChatCompletionService client;
        try
        {
            client = _factory.Get(normalizedProvider, normalizedModel);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create LLM client {Provider}:{Model}", normalizedProvider, normalizedModel);
            return (false, $"创建 {normalizedProvider} 客户端失败：{ex.Message}");
        }

        var probeSeconds = Math.Clamp(_options.TimeoutSeconds, 10, 45);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(probeSeconds));

            var history = new ChatHistory();
            history.AddUserMessage("请回复 ok");

            var settings = new OpenAIPromptExecutionSettings
            {
                MaxTokens = 8,
                Temperature = 0
            };

            await client
                .GetChatMessageContentsAsync(history, settings, kernel: null, cancellationToken: cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"验证 {normalizedProvider}:{normalizedModel} 超时（{probeSeconds} 秒），请检查网络或接口地址。");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LLM verification failed for {Provider}:{Model}", normalizedProvider, normalizedModel);
            return (false, $"验证 {normalizedProvider}:{normalizedModel} 失败：{ex.Message}");
        }

        try
        {
            var update = Builders<SettingsDoc>.Update
                .Set(s => s.Llm, new LlmSelection { Provider = normalizedProvider, Model = normalizedModel })
                .Set(s => s.UpdatedAt, DateTime.UtcNow);

            await _store.Settings
                .UpdateOneAsync(Builders<SettingsDoc>.Filter.Empty, update, new UpdateOptions { IsUpsert = true }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist LLM selection {Provider}:{Model}", normalizedProvider, normalizedModel);
            return (false, $"模型验证通过，但保存配置失败：{ex.Message}");
        }

        _factory.Invalidate();
        _selection.Set(new LlmSelection { Provider = normalizedProvider, Model = normalizedModel });

        return (true, $"已切换语言模型为 {normalizedProvider}:{normalizedModel}，验证请求成功。");
    }
}
