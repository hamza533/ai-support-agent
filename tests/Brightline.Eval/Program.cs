using System.Text.Json;
using Brightline.Agent;
using Brightline.Ai;
using Brightline.Data;
using Brightline.Data.Jobs;
using Brightline.Eval;
using Brightline.Knowledge;
using Brightline.LegacyOrders.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// make eval  →  dotnet run --project tests/Brightline.Eval [-- --only quality|scenarios] [--limit N]
// Needs the docker compose stack (make dev) and an AI provider key (Ai__Gemini__ApiKey in .env).
var root = EvalPaths.RepoRoot();
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(EvalDefaults.Settings(Environment.GetEnvironmentVariable("BRIGHTLINE_HOST") ?? "localhost"))
    .AddInMemoryCollection(EnvFile.Load(Path.Combine(root, ".env")))
    .AddEnvironmentVariables()
    .Build();

var only = Arg(args, "--only");
var limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : int.MaxValue;

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IConfiguration>(config);
services.AddSingleton(TimeProvider.System);
services.AddBrightlineData(config);
services.AddBrightlineAi(config);
services.AddBrightlineKnowledge(config);
services.AddBrightlineAgent(config);
services.AddLegacyOrdersClient(config);
services.AddSingleton<JobWorker>();
await using var provider = services.BuildServiceProvider();

var ai = config.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
Console.WriteLine($"Brightline eval · provider {ai.Provider} · model {ai.ChatModel} · judge {config["Eval:JudgeModel"]}");

var preflight = await Preflight.RunAsync(provider, config);
if (preflight is not null)
{
    await Console.Error.WriteLineAsync(preflight);
    return 2;
}

await KnowledgeIndex.EnsureSemanticIndexAsync(provider, Path.Combine(root, "sample_data", "help-center"));

var report = new EvalReport { StartedAt = DateTimeOffset.UtcNow, Provider = ai.Provider, Model = ai.ChatModel, JudgeModel = config["Eval:JudgeModel"] ?? ai.ChatModel };
var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
var scenarioDir = Path.Combine(AppContext.BaseDirectory, "Scenarios");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (only is null or "quality")
{
    var questions = JsonSerializer.Deserialize<QuestionSet>(await File.ReadAllTextAsync(Path.Combine(dataDir, "questions.json")), json)!;
    var quality = new QualityRunner(provider, config, Path.Combine(root, "eval", ".storage"));
    report.Quality = await quality.RunAsync(questions.Answerable.Take(limit).ToList());
    report.Unanswerable = await quality.RunUnanswerableAsync(questions.Unanswerable.Take(limit).ToList());
}

if (only is null or "scenarios")
{
    var scenarios = JsonSerializer.Deserialize<List<Scenario>>(await File.ReadAllTextAsync(Path.Combine(scenarioDir, "scenarios.json")), json)!;
    report.Scenarios = await new ScenarioRunner(provider, config).RunAsync(scenarios.Take(limit).ToList());
}

report.FinishedAt = DateTimeOffset.UtcNow;
report.Pricing = ai.Pricing;
await report.WriteAsync(Path.Combine(root, "eval"));
Console.WriteLine(report.Summary());
return report.MeetsTargets ? 0 : 1;

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
