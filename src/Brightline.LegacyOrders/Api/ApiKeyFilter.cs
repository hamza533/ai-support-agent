using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Brightline.LegacyOrders.Api;

/// <summary>Requires a shared API key in a request header (constant-time comparison).</summary>
public sealed class ApiKeyFilter(string headerName, Func<LegacyOptions, string> selectKey) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<LegacyOptions>>().Value;
        var expected = selectKey(options);
        var provided = context.HttpContext.Request.Headers[headerName].ToString();

        if (string.IsNullOrEmpty(expected) || !FixedTimeEquals(expected, provided))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or invalid API key.");
        }

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

public sealed class LegacyOptions
{
    public const string Section = "LegacyOrders";
    public string ApiKey { get; set; } = string.Empty;
    public string AdminKey { get; set; } = string.Empty;
    public bool MigrateOnStartup { get; set; }
    public bool SeedOnStartup { get; set; } = true;
}
