using System.Text.RegularExpressions;
using Brightline.Data;
using Brightline.Knowledge.Search;
using Brightline.LegacyOrders.Client;

namespace Brightline.Agent;

/// <summary>A numbered source the assistant may cite in the current turn.</summary>
public sealed record Citation(int Number, Guid SourceId, Guid ChunkId, string Title, string Uri, string HeadingPath);

/// <summary>
/// Server-side state of one customer conversation. The verified customer id lives ONLY here – tools read it from
/// this object and never accept it from the model.
/// </summary>
public sealed class ConversationSession
{
    public const int MaxVerificationAttempts = 5;

    private readonly List<Citation> _citations = [];
    private readonly List<ChatEvent> _pendingEvents = [];
    private readonly Lock _lock = new();

    public Guid ConversationId { get; set; } = Guid.NewGuid();

    public bool IsTest { get; set; }

    public bool IsPersisted { get; set; }

    public int? VerifiedCustomerId { get; private set; }

    public string? VerifiedFirstName { get; private set; }

    public string? VerifiedEmailMasked { get; private set; }

    public int FailedVerifications { get; private set; }

    public bool VerificationLocked => FailedVerifications >= MaxVerificationAttempts;

    public bool IsVerified => VerifiedCustomerId is not null;

    public IReadOnlyList<Citation> Citations
    {
        get
        {
            lock (_lock)
            {
                return [.. _citations];
            }
        }
    }

    /// <summary>Marks the conversation verified. A conversation can only ever belong to one customer.</summary>
    public bool TryVerify(VerifiedCustomer customer, string email)
    {
        if (VerifiedCustomerId is not null && VerifiedCustomerId != customer.CustomerId)
        {
            return false;
        }

        VerifiedCustomerId = customer.CustomerId;
        VerifiedFirstName = customer.FirstName;
        VerifiedEmailMasked = PiiMasker.MaskEmail(email);
        FailedVerifications = 0;
        return true;
    }

    public void RecordFailedVerification() => FailedVerifications++;

    /// <summary>Starts a new assistant turn: citation numbers restart at 1.</summary>
    public void BeginTurn()
    {
        lock (_lock)
        {
            _citations.Clear();
        }
    }

    public IReadOnlyList<Citation> RegisterSources(IEnumerable<SearchHit> hits)
    {
        lock (_lock)
        {
            var added = new List<Citation>();
            foreach (var hit in hits)
            {
                var existing = _citations.FirstOrDefault(c => c.ChunkId == hit.ChunkId);
                if (existing is not null)
                {
                    added.Add(existing);
                    continue;
                }

                var citation = new Citation(_citations.Count + 1, hit.SourceId, hit.ChunkId, hit.Title, hit.Uri, hit.HeadingPath);
                _citations.Add(citation);
                added.Add(citation);
            }

            return added;
        }
    }

    public void Publish(ChatEvent chatEvent)
    {
        lock (_lock)
        {
            _pendingEvents.Add(chatEvent);
        }
    }

    public IReadOnlyList<ChatEvent> DrainEvents()
    {
        lock (_lock)
        {
            var events = _pendingEvents.ToList();
            _pendingEvents.Clear();
            return events;
        }
    }
}

/// <summary>Wraps retrieved documents and API responses so the model treats them as data, never as instructions.</summary>
public static partial class UntrustedData
{
    public static string Wrap(string source, string content) =>
        $"<untrusted_data source=\"{Sanitize(source)}\">\n{Neutralize(content)}\n</untrusted_data>";

    /// <summary>Stops embedded text from closing the wrapper early or forging a new one.</summary>
    public static string Neutralize(string content) =>
        TagRegex().Replace(content, m => m.Value.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal));

    private static string Sanitize(string value) => new([.. value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' or '/')]);

    [GeneratedRegex(@"</?\s*untrusted_data[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();
}

/// <summary>Binds [n] markers in the answer to sources retrieved this turn; numbers that don't map are removed.</summary>
public static partial class CitationProcessor
{
    public static (string Text, IReadOnlyList<Citation> Used) Process(string text, IReadOnlyList<Citation> available)
    {
        var used = new List<Citation>();
        var cleaned = MarkerRegex().Replace(text, m =>
        {
            var numbers = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => int.TryParse(n, out var v) ? v : -1);
            var valid = numbers.Select(n => available.FirstOrDefault(c => c.Number == n)).OfType<Citation>().ToList();
            foreach (var c in valid.Where(c => !used.Contains(c)))
            {
                used.Add(c);
            }

            return string.Concat(valid.Select(c => $"[{c.Number}]"));
        });

        return (DoubleSpaceRegex().Replace(cleaned, " ").Replace(" .", ".", StringComparison.Ordinal), used.OrderBy(c => c.Number).ToList());
    }

    [GeneratedRegex(@"\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex DoubleSpaceRegex();
}
