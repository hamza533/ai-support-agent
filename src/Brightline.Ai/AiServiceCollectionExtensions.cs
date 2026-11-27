using System.ClientModel;
using System.ClientModel.Primitives;
using System.Threading.RateLimiting;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace Brightline.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary><c>gemini-openai-compat</c> (dev/demo), <c>azure-openai</c> (production), <c>openai</c>, or <c>fake</c> (tests).</summary>
    public string Provider { get; set; } = AiProviders.GeminiOpenAiCompat;
    public string ChatModel { get; set; } = "gemini-3.5-flash-lite";
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>Must match the <c>vector(768)</c> column. Changing it needs a migration and a full re-index.</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>Client-side throttle for free tiers (0 = unlimited).</summary>
    public int MaxRequestsPerMinute { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 3;
    public GeminiOptions Gemini { get; set; } = new();
    public AzureOpenAiOptions AzureOpenAI { get; set; } = new();
    public OpenAiOptions OpenAI { get; set; } = new();
    public PricingOptions Pricing { get; set; } = new();
}

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";
}

public sealed class AzureOpenAiOptions
{
    /// <summary>e.g. https://my-resource.openai.azure.com/ — auth is managed identity (DefaultAzureCredential), no key.</summary>
    public string Endpoint { get; set; } = string.Empty;
}

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Price per million tokens used for cost estimates (defaults: Azure OpenAI gpt-4.1-mini list price, USD).</summary>
public sealed class PricingOptions
{
    public decimal InputPerMillion { get; set; } = 0.40m;
    public decimal OutputPerMillion { get; set; } = 1.60m;

    public decimal Estimate(long inputTokens, long outputTokens) =>
        (inputTokens * InputPerMillion / 1_000_000m) + (outputTokens * OutputPerMillion / 1_000_000m);
}

public static class AiProviders
{
    public const string GeminiOpenAiCompat = "gemini-openai-compat";
    public const string AzureOpenAi = "azure-openai";
    public const string OpenAi = "openai";
    public const string Fake = "fake";
    public const string ActivitySource = "Brightline.Ai";
}

/// <summary>Thrown when the AI provider has no credentials yet (e.g. no Gemini API key in .env).</summary>
public sealed class AiNotConfiguredException(string message) : InvalidOperationException(message);

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> for the configured
    /// provider. Switching provider is configuration only. Both are wrapped with OpenTelemetry, logging and an optional
    /// free-tier throttle; the OpenAI SDK pipeline adds timeouts and retries with backoff.
    /// </summary>
    public static IServiceCollection AddBrightlineAi(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.Section);
        services.Configure<AiOptions>(section);
        var options = section.Get<AiOptions>() ?? new AiOptions();

        services.AddChatClient(sp => CreateChatClient(options))
            .Use(inner => new ThrottlingChatClient(inner, options.MaxRequestsPerMinute))
            .UseLogging()
            .UseOpenTelemetry(sourceName: AiProviders.ActivitySource, configure: c => c.EnableSensitiveData = false);

        services.AddEmbeddingGenerator(sp => CreateEmbeddingGenerator(options))
            .Use(inner => new ThrottlingEmbeddingGenerator(inner, options.MaxRequestsPerMinute))
            .UseOpenTelemetry(sourceName: AiProviders.ActivitySource);

        return services;
    }

    public static IChatClient CreateChatClient(AiOptions options) => options.Provider switch
    {
        AiProviders.Fake => new NotConfiguredChatClient("The AI provider is set to 'fake' (tests only)."),
        _ when !HasCredentials(options) => new NotConfiguredChatClient(MissingCredentialsMessage(options)),
        AiProviders.GeminiOpenAiCompat => new GeminiOptionsChatClient(CreateOpenAiClient(options).GetChatClient(options.ChatModel).AsIChatClient()),
        _ => CreateOpenAiClient(options).GetChatClient(options.ChatModel).AsIChatClient(),
    };

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(AiOptions options) => options.Provider switch
    {
        AiProviders.Fake => new NotConfiguredEmbeddingGenerator("The AI provider is set to 'fake' (tests only)."),
        _ when !HasCredentials(options) => new NotConfiguredEmbeddingGenerator(MissingCredentialsMessage(options)),
        _ => new DimensionGuardEmbeddingGenerator(
            CreateOpenAiClient(options).GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator(options.EmbeddingDimensions),
            options.EmbeddingDimensions),
    };

    /// <summary>All providers speak the OpenAI wire protocol, so one SDK covers them; only endpoint and auth differ.</summary>
    public static OpenAIClient CreateOpenAiClient(AiOptions options) => CreateOpenAiClient(options, transport: null);

    /// <summary>Same as <see cref="CreateOpenAiClient(AiOptions)"/>; tests pass a fake <paramref name="transport"/>.</summary>
    internal static OpenAIClient CreateOpenAiClient(AiOptions options, PipelineTransport? transport)
    {
        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(options.MaxRetries),
            UserAgentApplicationId = "brightline-support-agent",
        };

        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        switch (options.Provider)
        {
            case AiProviders.GeminiOpenAiCompat:
                clientOptions.Endpoint = new Uri(options.Gemini.BaseUrl);
                clientOptions.AddPolicy(new GeminiThoughtSignaturePolicy(), PipelinePosition.PerCall);
                return new OpenAIClient(new ApiKeyCredential(options.Gemini.ApiKey), clientOptions);

            case AiProviders.AzureOpenAi:
                // Azure OpenAI v1 API is OpenAI-compatible; Entra ID token via managed identity (no keys).
                // The AuthenticationPolicy constructor is marked evaluation-only (OPENAI001); it is the documented way to use Entra ID.
#pragma warning disable OPENAI001
                clientOptions.Endpoint = new Uri(options.AzureOpenAI.Endpoint.TrimEnd('/') + "/openai/v1/");
                return new OpenAIClient(
                    new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"),
                    clientOptions);
#pragma warning restore OPENAI001

            case AiProviders.OpenAi:
                return new OpenAIClient(new ApiKeyCredential(options.OpenAI.ApiKey), clientOptions);

            default:
                throw new InvalidOperationException(
                    $"Unknown Ai:Provider '{options.Provider}'. Use {AiProviders.GeminiOpenAiCompat}, {AiProviders.AzureOpenAi} or {AiProviders.OpenAi}.");
        }
    }

    private static bool HasCredentials(AiOptions o) => o.Provider switch
    {
        AiProviders.GeminiOpenAiCompat => !string.IsNullOrWhiteSpace(o.Gemini.ApiKey),
        AiProviders.AzureOpenAi => !string.IsNullOrWhiteSpace(o.AzureOpenAI.Endpoint),
        AiProviders.OpenAi => !string.IsNullOrWhiteSpace(o.OpenAI.ApiKey),
        _ => true, // unknown providers fail loudly in CreateOpenAiClient
    };

    private static string MissingCredentialsMessage(AiOptions o) => o.Provider switch
    {
        AiProviders.GeminiOpenAiCompat => "No Gemini API key: set Ai__Gemini__ApiKey in .env (https://aistudio.google.com/apikey).",
        AiProviders.AzureOpenAi => "No Azure OpenAI endpoint: set Ai__AzureOpenAI__Endpoint.",
        _ => "No OpenAI API key: set Ai__OpenAI__ApiKey.",
    };
}

/// <summary>Placeholder used when no credentials are configured: the app still starts; AI calls fail with a clear message.</summary>
internal sealed class NotConfiguredChatClient(string message) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new AiNotConfiguredException(message);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new AiNotConfiguredException(message);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("not-configured") : null;

    public void Dispose()
    {
    }
}

internal sealed class NotConfiguredEmbeddingGenerator(string message) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new AiNotConfiguredException(message);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Ensures vectors match the pgvector column. Gemini embeddings are Matryoshka-trained, so if an endpoint ignores the
/// requested dimension we truncate to the prefix and re-normalise (the documented way to shorten them).
/// </summary>
internal sealed class DimensionGuardEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, int dimensions)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new EmbeddingGenerationOptions();
        options.Dimensions ??= dimensions;
        var result = await base.GenerateAsync(values, options, cancellationToken);
        for (var i = 0; i < result.Count; i++)
        {
            var vector = result[i].Vector;
            if (vector.Length == dimensions)
            {
                continue;
            }

            if (vector.Length < dimensions)
            {
                throw new InvalidOperationException($"Embedding has {vector.Length} dimensions; the database expects {dimensions}.");
            }

            result[i] = new Embedding<float>(Truncate(vector.Span, dimensions)) { ModelId = result[i].ModelId, CreatedAt = result[i].CreatedAt };
        }

        return result;
    }

    internal static float[] Truncate(ReadOnlySpan<float> vector, int dimensions)
    {
        var prefix = vector[..dimensions].ToArray();
        var sumOfSquares = 0d;
        foreach (var v in prefix)
        {
            sumOfSquares += v * v;
        }

        var norm = (float)Math.Sqrt(sumOfSquares);
        if (norm > 0)
        {
            for (var i = 0; i < prefix.Length; i++)
            {
                prefix[i] /= norm;
            }
        }

        return prefix;
    }
}

/// <summary>Client-side request throttle (token bucket) for free-tier rate limits. 0 = disabled.</summary>
/// <summary>
/// Optional client-side throttle for free-tier rate limits. <see cref="QueuedTime"/> is the total time requests have
/// waited here, so the eval can report time to first token for the model rather than for this queue.
/// </summary>
public sealed class ThrottlingChatClient(IChatClient inner, int requestsPerMinute) : DelegatingChatClient(inner)
{
    private readonly RateLimiter? _limiter = Throttle.Create(requestsPerMinute);
    private long _queuedTicks;

    public TimeSpan QueuedTime => TimeSpan.FromTicks(Interlocked.Read(ref _queuedTicks));

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ThrottlingChatClient) ? this : base.GetService(serviceType, serviceKey);

    protected override void Dispose(bool disposing)
    {
        _limiter?.Dispose();
        base.Dispose(disposing);
    }

    private async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var lease = await Throttle.AcquireAsync(_limiter, ct);
        Interlocked.Add(ref _queuedTicks, System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks);
        return lease;
    }
}

internal sealed class ThrottlingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, int requestsPerMinute)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private readonly RateLimiter? _limiter = Throttle.Create(requestsPerMinute);

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var lease = await Throttle.AcquireAsync(_limiter, cancellationToken);
        return await base.GenerateAsync(values, options, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        _limiter?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class Throttle
{
    public static RateLimiter? Create(int perMinute) => perMinute <= 0 ? null : new TokenBucketRateLimiter(Options(perMinute));

    /// <summary>
    /// One token every 60/N seconds (bucket of N): a queued request waits a few seconds, not until the next full minute.
    /// </summary>
    internal static TokenBucketRateLimiterOptions Options(int perMinute) => new()
    {
        TokenLimit = perMinute,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
        QueueLimit = 1000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    public static async ValueTask<IDisposable> AcquireAsync(RateLimiter? limiter, CancellationToken ct)
    {
        if (limiter is null)
        {
            return NoLease.Instance;
        }

        var lease = await limiter.AcquireAsync(1, ct);
        return lease.IsAcquired ? lease : throw new InvalidOperationException("AI request throttle queue is full.");
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
