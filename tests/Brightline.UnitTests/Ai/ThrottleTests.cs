using Brightline.Ai;
using Microsoft.Extensions.AI;

namespace Brightline.UnitTests.Ai;

/// <summary>The free-tier throttle refills smoothly and reports how long requests queued (eval TTFT excludes it).</summary>
public sealed class ThrottleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Tokens_refill_one_at_a_time_instead_of_once_a_minute()
    {
        var options = Throttle.Options(10);

        Assert.Equal(10, options.TokenLimit);
        Assert.Equal(1, options.TokensPerPeriod);
        Assert.Equal(TimeSpan.FromSeconds(6), options.ReplenishmentPeriod);
    }

    [Fact]
    public async Task Time_spent_waiting_for_a_token_is_counted()
    {
        using var throttle = new ThrottlingChatClient(new EchoClient(), requestsPerMinute: 600); // 1 token per 100 ms
        for (var i = 0; i < 600; i++)
        {
            await throttle.GetResponseAsync("hi", cancellationToken: Ct); // drains the bucket without waiting
        }

        var before = throttle.QueuedTime;
        await throttle.GetResponseAsync("hi", cancellationToken: Ct); // has to wait for the next token

        Assert.True(throttle.QueuedTime - before > TimeSpan.FromMilliseconds(20), $"queued {(throttle.QueuedTime - before).TotalMilliseconds} ms");
    }

    [Fact]
    public void The_eval_finds_the_throttle_through_the_client_pipeline()
    {
        using var pipeline = new ChatClientBuilder(new EchoClient())
            .Use(inner => new ThrottlingChatClient(inner, 10))
            .UseOpenTelemetry()
            .Build();

        Assert.NotNull(pipeline.GetService<ThrottlingChatClient>());
    }

    private sealed class EchoClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
