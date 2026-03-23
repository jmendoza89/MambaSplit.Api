namespace MambaSplit.Api.Services;

internal static class EqualSplitCalculator
{
    public static IReadOnlyList<(Guid UserId, long AmountOwedCents)> Compute(
        long totalAmountCents,
        IEnumerable<Guid> memberIds)
    {
        var sorted = memberIds.OrderBy(x => x.ToString()).ToList();
        if (sorted.Count == 0)
            return Array.Empty<(Guid, long)>();

        var baseAmount = totalAmountCents / sorted.Count;
        var remainder = totalAmountCents % sorted.Count;

        return sorted
            .Select((id, i) => (id, baseAmount + (i < remainder ? 1L : 0L)))
            .ToArray();
    }
}