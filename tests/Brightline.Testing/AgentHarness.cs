using Brightline.Agent;
using Brightline.Ai;
using Brightline.Data;
using Brightline.Knowledge.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Brightline.Testing;

/// <summary>Builds a <see cref="ChatService"/> (or bare <see cref="SupportTools"/>) wired to fakes, no database needed.</summary>
public sealed class AgentHarness
{
    public FakeLegacyOrdersClient Orders { get; } = new();

    public IKnowledgeSearch Search { get; init; } = new FakeKnowledgeSearch();

    public InMemoryConversationStore Store { get; } = new();

    public AgentSettings Settings { get; init; } = new();

    public DemoOptions Demo { get; init; } = new();

    public ChatService CreateChat(IChatClient model) => new(
        model,
        Orders,
        Search,
        new StaticSettings(Settings),
        new NoDatabase(),
        Store,
        Options.Create(Demo),
        Options.Create(new AiOptions { ChatModel = "scripted-model" }),
        TimeProvider.System,
        NullLoggerFactory.Instance);

    public SupportTools CreateTools(ConversationSession session) => new(
        session, Orders, Search, Settings, new NoDatabase(), Store, TimeProvider.System, NullLogger<SupportTools>.Instance);

    public static async Task<List<ChatEvent>> CollectAsync(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }

    private sealed class StaticSettings(AgentSettings settings) : IAgentSettingsProvider
    {
        public Task<AgentSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);
    }

    /// <summary>Ticket tools need Postgres; they are covered by integration tests.</summary>
    private sealed class NoDatabase : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("No database in unit tests.");
    }
}
