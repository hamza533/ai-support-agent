using Brightline.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.UnitTests.Ai;

/// <summary>SPEC acceptance: switching Ai:Provider between Gemini and Azure OpenAI is configuration only.</summary>
public sealed class AiProviderConfigurationTests
{
    [Theory]
    [InlineData("gemini-openai-compat", "generativelanguage.googleapis.com", "/v1beta/openai/")]
    [InlineData("azure-openai", "contoso-ai.openai.azure.com", "/openai/v1/")]
    [InlineData("openai", "api.openai.com", "/v1")]
    public void The_same_code_builds_a_chat_client_for_each_provider(string provider, string host, string path)
    {
        using var sp = Build(new()
        {
            ["Ai:Provider"] = provider,
            ["Ai:ChatModel"] = "my-model",
            ["Ai:Gemini:ApiKey"] = "test-key",
            ["Ai:AzureOpenAI:Endpoint"] = "https://contoso-ai.openai.azure.com/",
            ["Ai:OpenAI:ApiKey"] = "test-key",
        });

        var chat = sp.GetRequiredService<IChatClient>();
        var metadata = chat.GetService<ChatClientMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal("my-model", metadata.DefaultModelId);
        Assert.Equal(host, metadata.ProviderUri!.Host);
        Assert.StartsWith(path, metadata.ProviderUri.AbsolutePath, StringComparison.Ordinal);
        Assert.NotNull(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public async Task Missing_credentials_give_a_clear_error_instead_of_crashing_startup()
    {
        using var sp = Build(new() { ["Ai:Provider"] = "gemini-openai-compat" });

        var chat = sp.GetRequiredService<IChatClient>();

        var ex = await Assert.ThrowsAsync<AiNotConfiguredException>(() => chat.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Ai__Gemini__ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_provider_fails_with_the_list_of_valid_values()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AiServiceCollectionExtensions.CreateOpenAiClient(new AiOptions { Provider = "bard" }));
        Assert.Contains("azure-openai", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_matryoshka_embeddings_are_truncated_and_renormalised()
    {
        var full = Enumerable.Range(1, 3072).Select(i => (float)i).ToArray();

        var truncated = DimensionGuardEmbeddingGenerator.Truncate(full, 768);

        Assert.Equal(768, truncated.Length);
        Assert.Equal(1.0, Math.Sqrt(truncated.Sum(v => (double)v * v)), 4);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBrightlineAi(config);
        return services.BuildServiceProvider();
    }
}
