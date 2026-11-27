using Microsoft.Extensions.AI;

namespace Brightline.Ai;

/// <summary>
/// Gemini's OpenAI-compatible endpoint rejects <c>seed</c> and <c>frequency_penalty</c> with 400 "Unknown name".
/// Microsoft.Extensions.AI.Evaluation's evaluators always send both, so they are dropped here for the Gemini provider
/// (a copy of the options is changed, never the caller's instance).
/// </summary>
public sealed class GeminiOptionsChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Supported(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Supported(options), cancellationToken);

    internal static ChatOptions? Supported(ChatOptions? options)
    {
        if (options is null || (options.Seed is null && options.FrequencyPenalty is null))
        {
            return options;
        }

        var copy = options.Clone();
        copy.Seed = null;
        copy.FrequencyPenalty = null;
        return copy;
    }
}
