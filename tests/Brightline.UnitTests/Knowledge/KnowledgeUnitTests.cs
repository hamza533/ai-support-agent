using System.IO.Compression;
using System.Text;
using Brightline.Data.Entities;
using Brightline.Knowledge.Parsing;
using Brightline.Knowledge.Search;

namespace Brightline.UnitTests.Knowledge;

public sealed class KnowledgeUnitTests
{
    [Fact]
    public void Markdown_sections_keep_their_heading_path_and_title()
    {
        const string md = "# Returns Policy\n\nIntro text.\n\n## Window\n\nThirty days.\n\n### Defects\n\nFree shipping.\n\n## Refunds\n\nOriginal method.";

        var doc = DocumentParser.ParseMarkdown(md, "fallback");

        Assert.Equal("Returns Policy", doc.Title);
        Assert.Equal(["", "Window", "Window > Defects", "Refunds"], doc.Sections.Select(s => s.HeadingPath));
        Assert.Contains("Free shipping.", doc.Sections[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunker_respects_max_size_and_keeps_sections_apart()
    {
        var longText = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"Sentence number {i} is here."));
        var sections = new[] { new ParsedSection("A", longText, null), new ParsedSection("B", "Short section.", 3) };

        var chunks = Chunker.Chunk(sections, maxChars: 500, overlapChars: 80);

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 600, $"chunk too long: {c.Text.Length}"));
        Assert.True(chunks.Count(c => c.HeadingPath == "A") > 5);
        var last = chunks[^1];
        Assert.Equal("B", last.HeadingPath);
        Assert.Equal(3, last.Page);
        Assert.Equal("Short section.", last.Text);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void File_type_is_detected_from_content_not_the_extension()
    {
        Assert.Equal(SourceType.Pdf, FileTypeDetector.Detect("x.md", "%PDF-1.7 ..."u8));
        Assert.Equal(SourceType.Markdown, FileTypeDetector.Detect("policy.md", "# Hello"u8));
        Assert.Null(FileTypeDetector.Detect("evil.md", [0x4D, 0x5A, 0x00, 0x01])); // binary renamed to .md
        Assert.Null(FileTypeDetector.Detect("notes.txt", "plain"u8));
        Assert.Equal(SourceType.Docx, FileTypeDetector.Detect("a.docx", MinimalDocx()));
        Assert.Null(FileTypeDetector.Detect("a.docx", MinimalZip("other.txt")));
    }

    [Fact]
    public void Reciprocal_rank_fusion_rewards_documents_found_by_both_retrievers()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        var fused = HybridSearch.Rrf(60, [a, b], [c, b]).ToList();

        Assert.Equal(b, fused[0].Id);
        Assert.Equal(3, fused.Count);
        Assert.Equal(2.0 / 62, fused[0].Score, 6); // rank 2 in both lists
    }

    [Fact]
    public void Full_text_query_uses_or_semantics_and_strips_operators()
    {
        Assert.Equal("can | return | headphones | if | one | side", HybridSearch.ToOrQuery("Can I return headphones if one side...?"));
        Assert.Equal(string.Empty, HybridSearch.ToOrQuery("!!! & | ()"));
        Assert.DoesNotContain("'", HybridSearch.ToOrQuery("o'reilly; DROP TABLE chunks"), StringComparison.Ordinal);
    }

    private static byte[] MinimalDocx() => MinimalZip("word/document.xml");

    private static byte[] MinimalZip(string entry)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
            writer.Write("<w:document/>");
        }

        return ms.ToArray();
    }
}
