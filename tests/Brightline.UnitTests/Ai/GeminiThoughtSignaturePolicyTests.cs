using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Brightline.Ai;
using Microsoft.Extensions.AI;

namespace Brightline.UnitTests.Ai;

/// <summary>Gemini 3 rejects a tool round-trip unless the tool call's thought_signature is sent back (found by the first live eval run).</summary>
public sealed class GeminiThoughtSignaturePolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ToolCallSse =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
        "\"extra_content\":{\"google\":{\"thought_signature\":\"SIG-1\"}},\"function\":{\"name\":\"search_knowledge_base\",\"arguments\":\"{\\\"query\\\":\\\"returns\\\"}\"}}]}}]}\r\n\r\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\r\n\r\n" +
        "data: [DONE]\r\n\r\n";

    private const string TextSse =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Returns are accepted within 30 days.\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task A_streamed_tool_round_trip_sends_the_signature_back()
    {
        var handler = new RecordingHandler(ToolCallSse, TextSse);
        var options = new AiOptions { Gemini = { ApiKey = "test-key" }, ChatModel = "gemini-test" };
        using var chat = AiServiceCollectionExtensions
            .CreateOpenAiClient(options, new HttpClientPipelineTransport(new HttpClient(handler)))
            .GetChatClient(options.ChatModel).AsIChatClient()
            .AsBuilder().UseFunctionInvocation().Build();
        var search = AIFunctionFactory.Create((string query) => "Returns are accepted within 30 days.", "search_knowledge_base");

        var text = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync("How long do I have to return?", new ChatOptions { Tools = [search] }, Ct))
        {
            text.Append(update.Text);
        }

        Assert.Equal("Returns are accepted within 30 days.", text.ToString());
        Assert.Equal(2, handler.Bodies.Count);
        var call = AssistantToolCalls(handler.Bodies[1]).Single();
        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("SIG-1", call["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    [Fact]
    public void A_buffered_response_is_captured_too()
    {
        var policy = new GeminiThoughtSignaturePolicy();
        policy.Capture("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call_9","extra_content":{"google":{"thought_signature":"SIG-9"}}}]}}]}""");

        var updated = policy.AddSignatures(Request(("call_9", null)))!;

        Assert.Equal("SIG-9", AssistantToolCalls(updated).Single()["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_steps_skip_validation_on_their_first_call_only()
    {
        var policy = new GeminiThoughtSignaturePolicy();

        var calls = AssistantToolCalls(policy.AddSignatures(Request(("a", null), ("b", null)))!);

        Assert.Equal(GeminiThoughtSignaturePolicy.SkipValidator, calls[0]["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
        Assert.Null(calls[1]["extra_content"]);
    }

    [Fact]
    public void Existing_signatures_and_plain_requests_are_left_alone()
    {
        var policy = new GeminiThoughtSignaturePolicy();

        Assert.Null(policy.AddSignatures(Request(("a", "REAL"))));
        Assert.Null(policy.AddSignatures("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""));
    }

    [Fact]
    public async Task The_capture_stream_passes_bytes_through_unchanged_even_when_lines_are_split()
    {
        var policy = new GeminiThoughtSignaturePolicy();
        var bytes = Encoding.UTF8.GetBytes(ToolCallSse);
        await using var stream = new GeminiThoughtSignaturePolicy.SseCaptureStream(new MemoryStream(bytes), policy);

        using var copy = new MemoryStream();
        var buffer = new byte[7]; // deliberately small: lines arrive in pieces
        int read;
        while ((read = await stream.ReadAsync(buffer, Ct)) > 0)
        {
            copy.Write(buffer, 0, read);
        }

        Assert.Equal(bytes, copy.ToArray());
        Assert.Equal(1, policy.Count);
    }

    private static string Request(params (string Id, string? Signature)[] calls)
    {
        var toolCalls = new JsonArray();
        foreach (var (id, signature) in calls)
        {
            var call = new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "t", ["arguments"] = "{}" } };
            if (signature is not null)
            {
                call["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = signature } };
            }

            toolCalls.Add(call);
        }

        return new JsonObject
        {
            ["model"] = "m",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "hi" },
                new JsonObject { ["role"] = "assistant", ["tool_calls"] = toolCalls }),
        }.ToJsonString();
    }

    private static List<JsonObject> AssistantToolCalls(string requestJson) =>
        [.. JsonNode.Parse(requestJson)!["messages"]!.AsArray().OfType<JsonObject>()
            .Where(m => m["role"]!.GetValue<string>() == "assistant")
            .SelectMany(m => m["tool_calls"]!.AsArray().OfType<JsonObject>())];

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_next++], Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}

/// <summary>Gemini's compatibility endpoint rejects seed and frequency_penalty; the AI.Evaluation judges send both.</summary>
public sealed class GeminiOptionsChatClientTests
{
    [Fact]
    public void Unsupported_options_are_dropped_on_a_copy()
    {
        var original = new ChatOptions { Seed = 0, FrequencyPenalty = 0, PresencePenalty = 0, Temperature = 0, MaxOutputTokens = 800 };

        var sent = GeminiOptionsChatClient.Supported(original)!;

        Assert.Null(sent.Seed);
        Assert.Null(sent.FrequencyPenalty);
        Assert.Equal(0f, sent.Temperature);
        Assert.Equal(800, sent.MaxOutputTokens);
        Assert.Equal(0, original.Seed); // the caller's options are untouched
    }

    [Fact]
    public void Options_without_them_pass_through_as_is()
    {
        var options = new ChatOptions { Temperature = 0.2f };
        Assert.Same(options, GeminiOptionsChatClient.Supported(options));
        Assert.Null(GeminiOptionsChatClient.Supported(null));
    }
}
