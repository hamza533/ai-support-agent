using System.IO.Compression;
using System.Text;
using Brightline.Data.Entities;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using UglyToad.PdfPig;

namespace Brightline.Knowledge.Parsing;

public sealed record ParsedSection(string HeadingPath, string Text, int? Page);

public sealed record ParsedDocument(string Title, IReadOnlyList<ParsedSection> Sections);

/// <summary>Detects the real file type from its bytes (magic numbers), not the file name.</summary>
public static class FileTypeDetector
{
    public static SourceType? Detect(string fileName, ReadOnlySpan<byte> content)
    {
        if (content.Length >= 5 && content[..5].SequenceEqual("%PDF-"u8))
        {
            return SourceType.Pdf;
        }

        if (content.Length >= 4 && content[..4].SequenceEqual("PK\u0003\u0004"u8))
        {
            return IsDocx(content.ToArray()) ? SourceType.Docx : null;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".md" or ".markdown" && IsUtf8Text(content))
        {
            return SourceType.Markdown;
        }

        return null;
    }

    private static bool IsDocx(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            return zip.GetEntry("word/document.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsUtf8Text(ReadOnlySpan<byte> content)
    {
        if (content.Contains((byte)0))
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public static class DocumentParser
{
    public static ParsedDocument Parse(SourceType type, byte[] content, string fallbackTitle) => type switch
    {
        SourceType.Markdown => ParseMarkdown(Encoding.UTF8.GetString(content), fallbackTitle),
        SourceType.Pdf => ParsePdf(content, fallbackTitle),
        SourceType.Docx => ParseDocx(content, fallbackTitle),
        _ => throw new NotSupportedException($"Unsupported document type {type}."),
    };

    /// <summary>Splits Markdown into sections under their heading path ("Returns &gt; Defective items").</summary>
    public static ParsedDocument ParseMarkdown(string markdown, string fallbackTitle)
    {
        var doc = Markdown.Parse(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var sections = new List<ParsedSection>();
        var headings = new List<(int Level, string Text)>();
        var buffer = new StringBuilder();
        string? title = null;

        void Flush()
        {
            var text = buffer.ToString().Trim();
            if (text.Length > 0)
            {
                sections.Add(new ParsedSection(string.Join(" > ", headings.Select(h => h.Text)), text, null));
            }

            buffer.Clear();
        }

        foreach (var block in doc)
        {
            if (block is HeadingBlock heading)
            {
                Flush();
                var text = InlineText(heading.Inline);
                if (heading.Level == 1 && title is null)
                {
                    title = text;
                    continue;
                }

                headings.RemoveAll(h => h.Level >= heading.Level);
                headings.Add((heading.Level, text));
                continue;
            }

            var blockText = BlockText(block, markdown).Trim();
            if (blockText.Length > 0)
            {
                buffer.AppendLine(blockText).AppendLine();
            }
        }

        Flush();
        return new ParsedDocument(title ?? fallbackTitle, sections);
    }

    public static ParsedDocument ParsePdf(byte[] content, string fallbackTitle)
    {
        using var pdf = PdfDocument.Open(content);
        var sections = new List<ParsedSection>();
        foreach (var page in pdf.GetPages())
        {
            var text = string.Join(' ', page.GetWords().Select(w => w.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add(new ParsedSection(string.Empty, text, page.Number));
            }
        }

        var title = pdf.Information.Title;
        return new ParsedDocument(string.IsNullOrWhiteSpace(title) ? fallbackTitle : title, sections);
    }

    public static ParsedDocument ParseDocx(byte[] content, string fallbackTitle)
    {
        using var stream = new MemoryStream(content);
        using var word = WordprocessingDocument.Open(stream, false);
        var body = word.MainDocumentPart?.Document?.Body;
        var sections = new List<ParsedSection>();
        var headings = new List<(int Level, string Text)>();
        var buffer = new StringBuilder();
        string? title = word.PackageProperties.Title;

        void Flush()
        {
            var text = buffer.ToString().Trim();
            if (text.Length > 0)
            {
                sections.Add(new ParsedSection(string.Join(" > ", headings.Select(h => h.Text)), text, null));
            }

            buffer.Clear();
        }

        foreach (var paragraph in body?.Descendants<Paragraph>() ?? [])
        {
            var text = paragraph.InnerText.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
            if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                title ??= text;
                continue;
            }

            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(style.AsSpan("Heading".Length), out var level))
            {
                Flush();
                headings.RemoveAll(h => h.Level >= level);
                headings.Add((level, text));
                continue;
            }

            buffer.AppendLine(text);
        }

        Flush();
        return new ParsedDocument(string.IsNullOrWhiteSpace(title) ? fallbackTitle : title, sections);
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var child in inline.Descendants())
        {
            switch (child)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string BlockText(Block block, string source) =>
        // Keep the original Markdown for the block (lists, tables, emphasis) – readable for both the LLM and FTS.
        block.Span.Length > 0 && block.Span.End < source.Length
            ? source.Substring(block.Span.Start, block.Span.Length)
            : block.Span.Length > 0 ? source[block.Span.Start..] : string.Empty;
}
