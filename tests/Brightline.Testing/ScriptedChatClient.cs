using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Brightline.Testing;

/// <summary>
/// A deterministic "model": each call returns the next scripted step. Steps can inspect the conversation (e.g. the
/// last tool result) to decide what to say. Records every request so tests can assert on what the model saw.
/// </summary>
public sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] steps) : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _steps = new(steps);

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 } };

    public static ChatResponse Call(string name, IDictionary<string, object?> args, string? callId = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId ?? $"call_{Guid.NewGuid():N}", name, args)]))
        {
            Usage = new UsageDetails { InputTokenCount = 80, OutputTokenCount = 10 },
        };

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Say(string text) => _ => Text(text);

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Invoke(string name, IDictionary<string, object?> args) => _ => Call(name, args);

    /// <summary>The most recent tool result the model received (or null).</summary>
    public static string? LastToolResult(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault()?.Result?.ToString();

    public int Remaining => _steps.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        Options.Add(options);
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("ScriptedChatClient ran out of steps.");
        }

        return Task.FromResult(_steps.Dequeue()(list));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", null, "scripted-model") : null;

    public void Dispose()
    {
    }
}
