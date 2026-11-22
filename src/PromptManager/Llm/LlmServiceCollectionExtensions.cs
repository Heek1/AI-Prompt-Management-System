namespace PromptManager.Llm;

public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddLlmClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.Section));
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.Section));

        // The SDK retries on its own; the named client only gives us pooled handlers.
        services.AddHttpClient(AnthropicLlmClient.ProviderName);

        // Singletons: clients are stateless and the Anthropic SDK client is meant to be reused.
        services.AddSingleton<ILlmClient, AnthropicLlmClient>();
        services.AddSingleton<ILlmClient, EchoLlmClient>();
        services.AddSingleton<LlmCatalog>();

        return services;
    }
}
