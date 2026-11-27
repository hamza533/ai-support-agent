using Brightline.Web.Infrastructure;

namespace Brightline.UnitTests.Web;

public sealed class DemoModeTests
{
    [Theory]
    [InlineData("2026-11-26T01:00:00Z", 2)]
    [InlineData("2026-11-26T03:00:00Z", 24)]
    [InlineData("2026-11-26T22:30:00Z", 4.5)]
    public void Nightly_reset_runs_at_the_configured_utc_hour(string now, double hoursUntil)
    {
        var delay = DemoResetService.DelayUntilNextReset(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), 3);

        Assert.Equal(hoursUntil, delay.TotalHours, 3);
    }

    [Theory]
    [InlineData("/admin/tickets", "/admin/tickets")]
    [InlineData("//evil.example", "/admin")]
    [InlineData("https://evil.example", "/admin")]
    [InlineData(@"/\evil.example", "/admin")]
    [InlineData(null, "/admin")]
    public void Return_urls_after_sign_in_stay_on_this_site(string? input, string expected) =>
        Assert.Equal(expected, AuthExtensions.SafeReturnUrl(input));

    [Fact]
    public void Csp_blocks_inline_scripts_and_framing()
    {
        Assert.Contains("script-src 'self';", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
    }
}
