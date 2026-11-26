using Brightline.Data;
using Brightline.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Brightline.Web.Infrastructure;

/// <summary>
/// One-shot commands run with the same image as the app (deploy steps, make targets):
/// <c>--migrate</c>, <c>--create-admin &lt;email&gt;</c> (password from ADMIN_PASSWORD or generated), <c>--seed</c>.
/// </summary>
public static class Commands
{
    public static bool IsCommand(string[] args) =>
        args.Any(a => a is "--migrate" or "--create-admin" or "--seed");

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Brightline.Commands");

        if (args.Contains("--migrate"))
        {
            await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<IdentityBootstrapper>().EnsureRolesAsync();
            logger.LogInformation("App database migrated");
        }

        var createAdmin = Array.IndexOf(args, "--create-admin");
        if (createAdmin >= 0)
        {
            if (createAdmin + 1 >= args.Length)
            {
                logger.LogError("Usage: --create-admin <email>");
                return 1;
            }

            var email = args[createAdmin + 1];
            var given = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
            var password = await sp.GetRequiredService<IdentityBootstrapper>().CreateUserAsync(email, Roles.Admin, given, "Administrator");
            if (string.IsNullOrEmpty(given))
            {
                // Printed once to the operator's terminal; never logged.
                await Console.Out.WriteLineAsync($"Admin {email} created. Generated password: {password}");
            }
        }

        if (args.Contains("--seed"))
        {
            foreach (var seeder in sp.GetServices<ISeeder>())
            {
                await seeder.SeedAsync(CancellationToken.None);
            }
        }

        return 0;
    }

    /// <summary>docker compose / dev convenience: migrate on start when <c>Database:MigrateOnStartup</c> is set.</summary>
    public static async Task MigrateIfConfiguredAsync(WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>().EnsureRolesAsync();
        foreach (var seeder in scope.ServiceProvider.GetServices<IStartupSeeder>())
        {
            await seeder.SeedAsync(CancellationToken.None);
        }
    }
}

/// <summary>Runs for <c>--seed</c> / <c>make seed</c>.</summary>
public interface ISeeder
{
    Task SeedAsync(CancellationToken ct);
}

/// <summary>Runs after start-up migrations (demo users, default documents when empty).</summary>
public interface IStartupSeeder
{
    Task SeedAsync(CancellationToken ct);
}
