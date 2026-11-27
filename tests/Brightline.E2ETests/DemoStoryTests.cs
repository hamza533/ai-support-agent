extern alias legacy;

using System.Net;
using System.Net.Sockets;
using Brightline.Agent;
using Brightline.Data.Entities;
using Brightline.Testing;
using Brightline.Web.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Npgsql;
using Testcontainers.PostgreSql;
using static Brightline.Testing.ScriptedChatClient;

namespace Brightline.E2ETests;

/// <summary>
/// SPEC acceptance: chat → verify → return request with confirmation → staff sees the ticket, in a real browser against
/// both real apps (web + legacy API over HTTP, real Postgres). The model is scripted so the run is deterministic and
/// needs no API key; everything else is production code.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DemoStoryTests : IAsyncLifetime
{
    private const string StaffEmail = "staff@brightline.test";
    private const string StaffPassword = "E2E-Staff-Password-1!";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg16").Build();
    private WebApplicationFactory<legacy::Brightline.LegacyOrders.Program>? _legacy;
    private WebApplicationFactory<Program>? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string _appUrl = string.Empty;
    private readonly List<string> _console = [];

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static ScriptedChatClient DemoStoryModel() => new(
        // Turn 1: policy question
        Invoke(SupportTools.SearchKnowledgeBaseName, Args(("query", "return defective headphones"))),
        Say("Yes — defective items can be returned within 30 days of delivery [1]. To check your order, please share your order number and the email on the order."),
        // Turn 2: verification + eligibility
        Invoke(SupportTools.GetOrderStatusName, Args(("orderNumber", "BL-10482"), ("email", "maya.t@example.com"))),
        Invoke(SupportTools.CheckReturnEligibilityName, Args(("orderNumber", "BL-10482"), ("sku", "AUD-AURORA-WH"), ("condition", "defective"))),
        Say("Thanks Maya, you're verified. Eligible — delivered 9 days ago, within the 30-day window, defect reported. Shall I create a return request?"),
        // Turn 3: return with confirmation
        Invoke(SupportTools.CreateReturnRequestName, Args(("orderNumber", "BL-10482"), ("sku", "AUD-AURORA-WH"), ("quantity", 1), ("condition", "defective"), ("reason", "left ear cuts out"))),
        messages => Text($"Done — {LastToolResult(messages)?.Split("\"returnNumber\":\"")[1].Split('"')[0]} created. Would you like me to open a support ticket for the defect?"),
        // Turn 4: ticket with confirmation
        Invoke(SupportTools.CreateSupportTicketName, Args(("subject", "Aurora headphones: left ear cuts out"), ("category", "warranty"), ("priority", "normal"), ("summary", "Customer reports the left ear of the Aurora Wireless Headphones cuts out. Return created."), ("orderNumber", "BL-10482"))),
        Say("Your ticket is open — our team replies within 1 business day."));

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        var appDb = await CreateDatabaseAsync("app");
        var legacyDb = await CreateDatabaseAsync("legacy");

        var legacyPort = FreePort();
        _legacy = new WebApplicationFactory<legacy::Brightline.LegacyOrders.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://127.0.0.1:{legacyPort}");
            b.UseSetting("ConnectionStrings:LegacyOrders", legacyDb);
            b.UseSetting("LegacyOrders:ApiKey", "e2e-key");
            b.UseSetting("LegacyOrders:AdminKey", "e2e-admin");
            b.UseSetting("LegacyOrders:MigrateOnStartup", "true");
        });
        _legacy.UseKestrel();
        _legacy.StartServer();

        var appPort = FreePort();
        _appUrl = $"http://127.0.0.1:{appPort}";
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting(WebHostDefaults.ServerUrlsKey, _appUrl);
            b.UseStaticWebAssets(); // package assets (_content/...) outside the Development environment
            b.UseSetting("ConnectionStrings:App", appDb);
            b.UseSetting("LegacyOrders:BaseUrl", $"http://127.0.0.1:{legacyPort}");
            b.UseSetting("LegacyOrders:ApiKey", "e2e-key");
            b.UseSetting("Database:MigrateOnStartup", "true");
            b.UseSetting("Worker:Mode", "embedded");
            b.UseSetting("Ai:Provider", "fake");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IChatClient>(DemoStoryModel());
                s.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
            });
        });
        _app.UseKestrel();
        _app.StartServer();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>().CreateUserAsync(StaffEmail, Roles.Staff, StaffPassword);
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    [Fact]
    public async Task Customer_returns_headphones_with_confirmation_and_staff_sees_the_ticket()
    {
        await WaitForIndexedArticlesAsync();
        var page = await _browser!.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        page.Console += (_, m) => _console.Add($"{m.Type}: {m.Text}");
        page.RequestFailed += (_, r) => _console.Add($"request failed: {r.Url} {r.Failure}");
        await page.GotoAsync(_appUrl);

        // Customer chat
        await OpenChatAsync(page);
        await SendAsync(page, "Can I return my headphones? The left ear cuts out.");
        await Assertions.Expect(page.Locator(".bubble a.cite").First).ToHaveTextAsync("1");
        await Assertions.Expect(page.GetByTestId("sources")).ToBeVisibleAsync();
        await ShotAsync(page, "02a-policy-answer");

        await SendAsync(page, "BL-10482, maya.t@example.com");
        await Assertions.Expect(page.GetByTestId("verified-chip")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("order-card")).ToContainTextAsync("Aurora Wireless Headphones");
        await ShotAsync(page, "02b-verified");

        await SendAsync(page, "Yes, please create the return.");
        var card = page.GetByTestId("approval-card");
        await Assertions.Expect(card).ToContainTextAsync("Return 1 × Aurora Wireless Headphones from order BL-10482");
        await Assertions.Expect(card).ToContainTextAsync("Nothing changes until you confirm.");
        await ShotAsync(page, "02c-confirmation-card");
        await page.GetByTestId("approval-confirm").ClickAsync();
        var returnDone = page.GetByTestId("action-completed").First;
        await Assertions.Expect(returnDone).ToContainTextAsync("RR-");

        await SendAsync(page, "Yes, open a ticket for the defect.");
        await Assertions.Expect(page.GetByTestId("approval-card").Last).ToContainTextAsync("Open a support ticket");
        await page.GetByTestId("approval-confirm").Last.ClickAsync();
        var ticketDone = page.GetByTestId("action-completed").Last;
        await Assertions.Expect(ticketDone).ToContainTextAsync("TK-");
        var ticketNumber = System.Text.RegularExpressions.Regex.Match(await ticketDone.InnerTextAsync(), @"TK-\d+").Value;
        await ShotAsync(page, "02d-done");

        // Staff sees the ticket
        var staff = await _browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        await staff.GotoAsync($"{_appUrl}/admin/tickets");
        await staff.GetByLabel("Email").FillAsync(StaffEmail);
        await staff.GetByLabel("Password").FillAsync(StaffPassword);
        await staff.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(staff.GetByTestId("ticket-table")).ToContainTextAsync(ticketNumber);
        await Assertions.Expect(staff.GetByTestId("ticket-table")).ToContainTextAsync("Aurora headphones: left ear cuts out");
        await ShotAsync(staff, "05-tickets");

        // ...and the audit trail shows the approved write
        await staff.GetByTestId("ticket-table").GetByRole(AriaRole.Link, new() { Name = "View" }).First.ClickAsync();
        await Assertions.Expect(staff.Locator(".tid-agent-trace")).ToContainTextAsync("create_return_request");
        await Assertions.Expect(staff.Locator(".tid-agent-trace")).ToContainTextAsync("Approved by customer");
        await Assertions.Expect(staff.Locator(".tid-transcript a.cite").First).ToHaveTextAsync("1"); // rendered, not escaped HTML
        await Assertions.Expect(staff.Locator(".tid-transcript")).Not.ToContainTextAsync("<p>");
        await ShotAsync(staff, "04-conversation-detail");
        await staff.GotoAsync($"{_appUrl}/admin");
        await Assertions.Expect(staff.GetByTestId("stats")).ToBeVisibleAsync();
        await ShotAsync(staff, "03-admin-overview");
        await staff.GotoAsync($"{_appUrl}/admin/documents");
        await Assertions.Expect(staff.GetByTestId("document-table")).ToBeVisibleAsync();
        await ShotAsync(staff, "06-documents");
    }

    /// <summary>The page is prerendered; the button works once the Blazor circuit is connected, so retry until the panel opens.</summary>
    private async Task OpenChatAsync(IPage page)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await page.GetByTestId("open-chat").IsVisibleAsync())
            {
                await page.GetByTestId("open-chat").ClickAsync();
            }

            try
            {
                await page.GetByTestId("chat-input").WaitForAsync(new() { Timeout = 1000 });
                return;
            }
            catch (TimeoutException)
            {
                // circuit not connected yet
            }
        }

        throw new TimeoutException("The chat panel did not open. Console: " + string.Join(" | ", _console));
    }

    /// <summary>With E2E_SCREENSHOTS=1, saves docs/images/{name}.png (used by the README).</summary>
    private static async Task ShotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") != "1")
        {
            return;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Brightline.slnx")))
        {
            dir = dir.Parent;
        }

        var images = Path.Combine(dir!.FullName, "docs", "images");
        Directory.CreateDirectory(images);
        await page.WaitForTimeoutAsync(300); // let streaming text and toasts settle
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, name + ".png") });
    }

    private static async Task SendAsync(IPage page, string text)
    {
        var input = page.GetByTestId("chat-input");
        await Assertions.Expect(input).ToBeEnabledAsync();
        await input.FillAsync(text);
        await page.GetByTestId("chat-send").ClickAsync();
    }

    private async Task WaitForIndexedArticlesAsync()
    {
        using var http = new HttpClient();
        for (var i = 0; i < 60; i++)
        {
            var home = await http.GetStringAsync(_appUrl);
            if (home.Contains("article-list", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name, Timeout = 60 }.ConnectionString;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        if (_legacy is not null)
        {
            await _legacy.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
