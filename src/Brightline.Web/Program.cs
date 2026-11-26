var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/", () => "Brightline AI Support Agent");

app.Run();

/// <summary>Entry point, exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
