using System.Reflection;
using System.Text.Json;

namespace Brightline.UnitTests.Architecture;

/// <summary>
/// The legacy Orders API stands in for an existing system: the app may reach it only over HTTP through
/// Brightline.LegacyOrders.Client, never by referencing its assembly or its database.
/// </summary>
public sealed class LegacyBoundaryTests
{
    private static readonly string[] _appAssemblies =
    [
        "Brightline.Web",
        "Brightline.Agent",
        "Brightline.Ai",
        "Brightline.Knowledge",
        "Brightline.Data",
        "Brightline.LegacyOrders.Client",
    ];

    [Fact]
    public void App_assemblies_do_not_reference_the_legacy_api_assembly()
    {
        var offenders = new List<string>();
        foreach (var name in _appAssemblies)
        {
            var assembly = Assembly.Load(name);
            offenders.AddRange(assembly.GetReferencedAssemblies()
                .Where(r => r.Name == "Brightline.LegacyOrders")
                .Select(_ => name));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void App_configuration_does_not_contain_the_legacy_connection_string()
    {
        var root = FindRepoRoot();
        var webDir = Path.Combine(root, "src", "Brightline.Web");
        var files = Directory.EnumerateFiles(webDir, "appsettings*.json", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings))
            {
                Assert.False(
                    connectionStrings.TryGetProperty("LegacyOrders", out _),
                    $"{Path.GetFileName(file)} must not contain ConnectionStrings:LegacyOrders");
            }
        }
    }

    [Fact]
    public void App_source_does_not_reference_the_legacy_database()
    {
        var root = FindRepoRoot();
        var appSourceDirs = _appAssemblies.Select(a => Path.Combine(root, "src", a));
        var hits = appSourceDirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ConnectionStrings:LegacyOrders", StringComparison.Ordinal)
                     || File.ReadAllText(f).Contains("GetConnectionString(\"LegacyOrders\")", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(hits);
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Brightline.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Brightline.slnx) not found.");
    }
}
