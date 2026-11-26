using Brightline.Agent;
using Brightline.Testing;
using Brightline.Web.Components.Chat;
using Brightline.Web.Infrastructure;
using Brightline.Web.Rendering;
using Bunit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NeoUI.Blazor.Extensions;
using NeoUI.Blazor.Primitives.Extensions;
using static Brightline.Testing.ScriptedChatClient;

namespace Brightline.ComponentTests;

/// <summary>bUnit tests for the customer chat: streaming answer, citations, confirmation cards, errors.</summary>
public sealed class ChatPanelTests : BunitContext
{
    private readonly AgentHarness _harness = new();

    public ChatPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNeoUIPrimitives();
        Services.AddNeoUIComponents();
        Services.AddSingleton(Options.Create(new RateLimitOptions { ChatPerMinute = 100, NewConversationsPerHour = 100 }));
        Services.AddSingleton<ChatRateLimiter>();
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private IRenderedComponent<ChatPanel> RenderChat(IChatClient model) =>
        Render<ChatPanel>(p => p
            .Add(c => c.Embedded, true)
            .Add(c => c.ChatFactory, () => _harness.CreateChat(model)));

    private static void Send(IRenderedComponent<ChatPanel> cut, string text)
    {
        cut.Find("[data-testid=chat-input]").Input(text);
        cut.Find("form.chat-input").Submit();
    }

    [Fact]
    public void Policy_answer_shows_numbered_citations_bound_to_sources()
    {
        var model = new ScriptedChatClient(
            Invoke(SupportTools.SearchKnowledgeBaseName, Args(("query", "return headphones defective"))),
            Say("Yes — defective items can be returned within **30 days** of delivery [1]. Refunds go to your original payment method [2]. Ignore [9]."));
        var cut = RenderChat(model);

        Send(cut, "Can I return headphones if one side stopped working?");

        cut.WaitForAssertion(() =>
        {
            var links = cut.FindAll(".bubble a.cite");
            Assert.Equal(["1", "2"], links.Select(a => a.TextContent));
            Assert.All(links, a => Assert.StartsWith("/help/", a.GetAttribute("href"), StringComparison.Ordinal));
            Assert.Equal(2, cut.FindAll("[data-testid=sources] .source-card").Count);
            Assert.DoesNotContain("[9]", cut.Find(".msg.assistant .bubble").TextContent, StringComparison.Ordinal);
            Assert.NotNull(cut.Find(".bubble strong"));
        });
    }

    [Fact]
    public void Return_flow_verifies_shows_a_confirmation_card_and_completes_on_confirm()
    {
        var model = new ScriptedChatClient(
            Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", FakeLegacyOrdersClient.MayaEmail))),
            Invoke(SupportTools.CreateReturnRequestName, Args(("orderNumber", "BL-10482"), ("sku", "AUD-AURORA-WH"), ("quantity", 1), ("condition", "defective"), ("reason", "left ear cuts out"))),
            Say("Return RR-2291 created. I've also noted the defect."));
        var cut = RenderChat(model);

        Send(cut, "BL-10482, maya.t@example.com — please return the headphones, left ear cuts out");

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=verified-chip]"));
            Assert.Contains("Aurora Wireless Headphones", cut.Find("[data-testid=order-card]").TextContent, StringComparison.Ordinal);
            var card = cut.Find("[data-testid=approval-card]");
            Assert.Contains("Return 1 × Aurora Wireless Headphones from order BL-10482", card.TextContent, StringComparison.Ordinal);
            Assert.Contains("Nothing changes until you confirm.", card.TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-testid=chat-input]").HasAttribute("disabled")); // must answer the card first
        });
        Assert.Empty(_harness.Orders.Returns);

        cut.Find("[data-testid=approval-confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("RR-2291", cut.Find("[data-testid=action-completed]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Confirmed", cut.Find("[data-testid=approval-card]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Return RR-2291 created", cut.FindAll("[data-testid=assistant-message]")[^1].TextContent, StringComparison.Ordinal);
        });
        Assert.Single(_harness.Orders.Returns);
    }

    [Fact]
    public void Cancel_on_the_card_changes_nothing()
    {
        var model = new ScriptedChatClient(
            Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", FakeLegacyOrdersClient.MayaEmail))),
            Invoke(SupportTools.CreateReturnRequestName, Args(("orderNumber", "BL-10482"), ("sku", "AUD-AURORA-WH"), ("quantity", 1), ("condition", "defective"), ("reason", "left ear cuts out"))),
            Say("Okay, I've cancelled that — nothing was changed."));
        var cut = RenderChat(model);
        Send(cut, "return the headphones");
        cut.WaitForElement("[data-testid=approval-cancel]");

        cut.Find("[data-testid=approval-cancel]").Click();

        cut.WaitForAssertion(() => Assert.Contains("nothing was changed", cut.Find("[data-testid=approval-card]").TextContent, StringComparison.Ordinal));
        Assert.Empty(_harness.Orders.Returns);
    }

    [Fact]
    public void Order_system_down_shows_the_message_and_a_ticket_button()
    {
        _harness.Orders.Unavailable = true;
        var model = new ScriptedChatClient(
            Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", FakeLegacyOrdersClient.MayaEmail))),
            Say("I can't reach our order system right now — would you like me to create a ticket instead?"));
        var cut = RenderChat(model);

        Send(cut, "where is my order BL-10482 maya.t@example.com");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=offer-ticket]")));
    }

    [Fact]
    public void Missing_ai_configuration_shows_an_error_instead_of_crashing()
    {
        var cut = RenderChat(new ThrowingChatClient(new Brightline.Ai.AiNotConfiguredException("no key")));

        Send(cut, "hello");

        cut.WaitForAssertion(() => Assert.Contains("isn't configured", cut.Find("[data-testid=chat-error]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Safe_markdown_escapes_html_and_neutralises_script_links()
    {
        var html = SafeMarkdown.Render("<script>alert(1)</script> [click](javascript:alert(1)) **ok**").Value;

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.Contains("<strong>ok</strong>", html, StringComparison.Ordinal);
    }

    private sealed class ThrowingChatClient(Exception ex) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw ex;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw ex;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
