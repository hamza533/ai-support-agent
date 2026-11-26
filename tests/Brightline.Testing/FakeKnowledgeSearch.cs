using Brightline.Knowledge.Search;

namespace Brightline.Testing;

/// <summary>Returns fixed help-centre hits (optionally containing a prompt-injection attempt).</summary>
public sealed class FakeKnowledgeSearch(params SearchHit[] hits) : IKnowledgeSearch
{
    public static readonly Guid ReturnsSource = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static SearchHit ReturnPolicyHit { get; } = new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), ReturnsSource, "Returns & Refunds Policy", $"/help/{ReturnsSource}",
        "Return window", null, "You can return an item within 30 days of the delivery date if it is unopened or defective.", 0.03);

    public static SearchHit RefundHit { get; } = new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), ReturnsSource, "Returns & Refunds Policy", $"/help/{ReturnsSource}",
        "Refunds", null, "Refunds go to the original payment method after inspection.", 0.02);

    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int? top = null, CancellationToken ct = default)
    {
        Queries.Add(query);
        IReadOnlyList<SearchHit> result = hits.Length > 0 ? hits : [ReturnPolicyHit, RefundHit];
        return Task.FromResult(result);
    }
}
