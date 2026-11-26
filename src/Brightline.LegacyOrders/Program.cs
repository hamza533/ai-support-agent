var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

namespace Brightline.LegacyOrders
{
    /// <summary>Entry point, exposed for WebApplicationFactory in tests.</summary>
    public partial class Program;
}
