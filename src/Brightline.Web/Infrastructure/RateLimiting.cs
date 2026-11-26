using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Brightline.Web.Infrastructure;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";
    public int ChatPerMinute { get; set; } = 20;
    public int LoginPerMinute { get; set; } = 10;
    public int NewConversationsPerHour { get; set; } = 30;
}

/// <summary>
/// Chat messages travel over the Blazor SignalR circuit, not individual HTTP requests, so the chat limiter is a
/// partitioned limiter the chat service calls per message (keyed by client IP). HTTP endpoints (login, hub
/// negotiation) use the ASP.NET Core rate limiting middleware.
/// </summary>
public sealed class ChatRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _messages;
    private readonly PartitionedRateLimiter<string> _conversations;

    public ChatRateLimiter(Microsoft.Extensions.Options.IOptions<RateLimitOptions> options)
    {
        var o = options.Value;
        _messages = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = o.ChatPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        _conversations = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = o.NewConversationsPerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    }

    public bool TryAcquireMessage(string clientKey)
    {
        using var lease = _messages.AttemptAcquire(clientKey);
        return lease.IsAcquired;
    }

    public bool TryAcquireConversation(string clientKey)
    {
        using var lease = _conversations.AttemptAcquire(clientKey);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _messages.Dispose();
        _conversations.Dispose();
    }
}

public static class RateLimitingExtensions
{
    public const string LoginPolicy = "login";

    public static IServiceCollection AddBrightlineRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));
        services.AddSingleton<ChatRateLimiter>();
        var options = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var isLoginPost = HttpMethods.IsPost(ctx.Request.Method)
                    && ctx.Request.Path.StartsWithSegments("/account/login", StringComparison.OrdinalIgnoreCase);
                if (!isLoginPost)
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                return RateLimitPartition.GetFixedWindowLimiter("login:" + ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.LoginPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    public static string ClientKey(HttpContext? ctx) =>
        ctx?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
