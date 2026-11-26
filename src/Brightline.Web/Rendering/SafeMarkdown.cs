using System.Net;
using System.Text.RegularExpressions;
using Brightline.Agent;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Brightline.Web.Rendering;

/// <summary>
/// Renders model and document Markdown safely: raw HTML is escaped (DisableHtml), links are limited to http(s) and
/// relative URLs, and [n] markers become links to the cited help article.
/// </summary>
public static partial class SafeMarkdown
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    public static MarkupString Render(string? markdown, IReadOnlyList<Citation>? citations = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        var doc = Markdown.Parse(markdown, _pipeline);
        foreach (var link in doc.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url))
            {
                link.Url = "#";
            }
        }

        var html = doc.ToHtml(_pipeline);
        if (citations is { Count: > 0 })
        {
            html = CitationRegex().Replace(html, m =>
            {
                var n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var c = citations.FirstOrDefault(x => x.Number == n);
                return c is null
                    ? m.Value
                    : $"<a class=\"cite\" href=\"{WebUtility.HtmlEncode(c.Uri)}\" title=\"{WebUtility.HtmlEncode(c.Title)}\" target=\"_blank\" rel=\"noopener\" aria-label=\"Source {n}: {WebUtility.HtmlEncode(c.Title)}\">{n}</a>";
            });
        }

        return new MarkupString(html);
    }

    internal static bool IsSafeUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
            || url.StartsWith('#'));

    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex CitationRegex();
}
