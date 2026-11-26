using System.Text;

namespace Brightline.Knowledge.Parsing;

public sealed record TextChunk(int Index, string HeadingPath, string Text, int? Page);

/// <summary>
/// Splits sections into ~<c>maxChars</c> chunks on paragraph, then sentence boundaries, with a small overlap.
/// Chunks never cross a section boundary, so each keeps its heading path for citations.
/// </summary>
public static class Chunker
{
    public static IReadOnlyList<TextChunk> Chunk(IEnumerable<ParsedSection> sections, int maxChars = 1000, int overlapChars = 150)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 200);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapChars);

        var chunks = new List<TextChunk>();
        foreach (var section in sections)
        {
            foreach (var text in SplitSection(section.Text, maxChars, overlapChars))
            {
                chunks.Add(new TextChunk(chunks.Count, section.HeadingPath, text, section.Page));
            }
        }

        return chunks;
    }

    private static IEnumerable<string> SplitSection(string text, int maxChars, int overlapChars)
    {
        var units = Units(text, maxChars).ToList();
        var current = new StringBuilder();
        foreach (var unit in units)
        {
            if (current.Length > 0 && current.Length + unit.Length + 1 > maxChars)
            {
                var done = current.ToString().Trim();
                yield return done;
                current.Clear();
                var overlap = Tail(done, overlapChars);
                if (overlap.Length > 0)
                {
                    current.Append(overlap).Append(' ');
                }
            }

            current.Append(unit).Append(unit.EndsWith('\n') ? string.Empty : "\n");
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            yield return last;
        }
    }

    /// <summary>Paragraphs, or sentences for over-long paragraphs, or hard slices for over-long sentences.</summary>
    private static IEnumerable<string> Units(string text, int maxChars)
    {
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (paragraph.Length <= maxChars)
            {
                yield return paragraph;
                continue;
            }

            foreach (var sentence in Sentences(paragraph))
            {
                if (sentence.Length <= maxChars)
                {
                    yield return sentence;
                    continue;
                }

                for (var i = 0; i < sentence.Length; i += maxChars)
                {
                    yield return sentence.Substring(i, Math.Min(maxChars, sentence.Length - i));
                }
            }
        }
    }

    private static IEnumerable<string> Sentences(string paragraph)
    {
        var start = 0;
        for (var i = 0; i < paragraph.Length - 1; i++)
        {
            if (paragraph[i] is '.' or '!' or '?' && char.IsWhiteSpace(paragraph[i + 1]))
            {
                yield return paragraph[start..(i + 1)].Trim();
                start = i + 1;
            }
        }

        if (start < paragraph.Length)
        {
            yield return paragraph[start..].Trim();
        }
    }

    private static string Tail(string text, int chars)
    {
        // Short chunks are not repeated: overlap only carries context out of long ones.
        if (chars == 0 || text.Length <= chars * 2)
        {
            return string.Empty;
        }

        var tail = text[^chars..];
        var space = tail.IndexOf(' ', StringComparison.Ordinal);
        return space >= 0 ? tail[(space + 1)..] : tail;
    }
}
