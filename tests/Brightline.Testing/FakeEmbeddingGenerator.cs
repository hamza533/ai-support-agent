using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Brightline.Testing;

/// <summary>
/// Deterministic, offline embedding generator (feature hashing of word stems into 768 buckets, L2-normalised).
/// Texts that share words get similar vectors, which is enough to test retrieval without an API key.
/// </summary>
public sealed partial class FakeEmbeddingGenerator(int dimensions = 768) : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly HashSet<string> _stopWords =
    [
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "be", "can", "i", "my", "you", "your",
        "it", "if", "with", "what", "how", "do", "does", "we", "our", "at", "by", "from", "this", "that", "as", "not",
    ];

    public int Calls { get; private set; }

    public bool Fail { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Fail)
        {
            throw new HttpRequestException("Fake embedding endpoint is down.");
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v))));
        return Task.FromResult(result);
    }

    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (Match m in WordRegex().Matches(text.ToLowerInvariant()))
        {
            var word = Stem(m.Value);
            if (word.Length < 2 || _stopWords.Contains(word))
            {
                continue;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word));
            var bucket = (int)(BitConverter.ToUInt32(hash, 0) % (uint)dimensions);
            vector[bucket] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        else
        {
            vector[0] = 1f; // never return a zero vector (cosine undefined)
        }

        return vector;
    }

    private static string Stem(string word) =>
        word.Length > 4 && word.EndsWith('s') ? word[..^1] : word;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();
}
