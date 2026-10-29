using Microsoft.Extensions.DependencyInjection;

namespace Msg2Dc.Llm;

public static class LlmModule
{
    public static IServiceCollection AddLlmModule(this IServiceCollection services)
    {
        services.AddSingleton<LlmSelectionProvider>();
        services.AddSingleton<LlmClientFactory>();
        services.AddSingleton<ILlmClassifier, LlmClassifier>();
        services.AddSingleton<ILlmModelSwitcher, LlmModelSwitcher>();
        return services;
    }
}
