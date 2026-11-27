using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brightline.Ai;

/// <summary>
/// Gemini 3 models attach a <c>thought_signature</c> to each tool call (<c>tool_calls[].extra_content.google</c>) and
/// reject the next request with 400 unless it is sent back. The OpenAI SDK drops that unknown field, so this policy
/// records signatures from responses (streamed and buffered) by call id and puts them back on outgoing
/// <c>tool_calls</c>. If a signature is unknown (e.g. the process restarted mid-conversation), the first call of that
/// step gets Google's documented validator-skip value instead of failing the turn.
/// See https://ai.google.dev/gemini-api/docs/thought-signatures.
/// </summary>
public sealed class GeminiThoughtSignaturePolicy : PipelinePolicy
{
    /// <summary>Documented by Google for function calls that have no real signature.</summary>
    public const string SkipValidator = "skip_thought_signature_validator";

    private const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public int Count => _signatures.Count;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        BeforeSend(message);
        ProcessNext(message, pipeline, currentIndex);
        AfterReceive(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        BeforeSend(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        AfterReceive(message);
    }

    public void Remember(string callId, string signature)
    {
        if (_signatures.TryAdd(callId, signature))
        {
            _order.Enqueue(callId);
            while (_order.Count > MaxEntries && _order.TryDequeue(out var oldest))
            {
                _signatures.TryRemove(oldest, out _);
            }
        }
    }

    /// <summary>Adds signatures to every assistant tool call in a chat-completions request body. Returns null if nothing changed.</summary>
    public string? AddSignatures(string requestJson)
    {
        if (JsonNode.Parse(requestJson) is not JsonObject body || body["messages"] is not JsonArray messages)
        {
            return null;
        }

        var changed = false;
        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (msg["role"]?.GetValue<string>() != "assistant" || msg["tool_calls"] is not JsonArray calls || calls.Count == 0)
            {
                continue;
            }

            var signed = false;
            foreach (var call in calls.OfType<JsonObject>())
            {
                if (SignatureOf(call) is not null)
                {
                    signed = true;
                    continue;
                }

                if (call["id"]?.GetValue<string>() is { } id && _signatures.TryGetValue(id, out var signature))
                {
                    SetSignature(call, signature);
                    signed = changed = true;
                }
            }

            // Gemini signs only the first call of a parallel step; if we know none of them, skip validation for the step.
            if (!signed && calls[0] is JsonObject first)
            {
                SetSignature(first, SkipValidator);
                changed = true;
            }
        }

        return changed ? body.ToJsonString() : null;
    }

    /// <summary>Records signatures from a chat-completions response body or one SSE <c>data:</c> payload.</summary>
    public void Capture(string json)
    {
        if (!json.Contains("thought_signature", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["choices"] is not JsonArray choices)
            {
                return;
            }

            foreach (var choice in choices.OfType<JsonObject>())
            {
                var calls = (choice["message"] ?? choice["delta"])?["tool_calls"] as JsonArray;
                foreach (var call in calls?.OfType<JsonObject>() ?? [])
                {
                    if (call["id"]?.GetValue<string>() is { Length: > 0 } id && SignatureOf(call) is { Length: > 0 } signature)
                    {
                        Remember(id, signature);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON we understand; the request will fall back to the validator-skip value.
        }
    }

    private static string? SignatureOf(JsonObject call) =>
        call["extra_content"]?["google"]?["thought_signature"]?.GetValue<string>();

    private static void SetSignature(JsonObject call, string signature) =>
        call["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = signature } };

    private static bool IsChatCompletions(PipelineMessage message) =>
        message.Request.Uri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true;

    private void BeforeSend(PipelineMessage message)
    {
        if (!IsChatCompletions(message) || message.Request.Content is not { } content)
        {
            return;
        }

        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        var updated = AddSignatures(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        if (updated is not null)
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(updated));
        }
    }

    private void AfterReceive(PipelineMessage message)
    {
        if (!IsChatCompletions(message) || message.Response is not { IsError: false } response)
        {
            return;
        }

        if (message.BufferResponse)
        {
            Capture(response.Content.ToString());
        }
        else if (response.ContentStream is { } stream)
        {
            response.ContentStream = new SseCaptureStream(stream, this);
        }
    }

    /// <summary>Passes a server-sent-events stream through unchanged while feeding each <c>data:</c> line to <see cref="Capture"/>.</summary>
    internal sealed class SseCaptureStream(Stream inner, GeminiThoughtSignaturePolicy policy) : Stream
    {
        private readonly MemoryStream _line = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Scan(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Scan(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _line.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Scan(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                if (b != (byte)'\n')
                {
                    _line.WriteByte(b);
                    continue;
                }

                var line = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                _line.SetLength(0);
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    policy.Capture(line[5..].Trim());
                }
            }
        }
    }
}
