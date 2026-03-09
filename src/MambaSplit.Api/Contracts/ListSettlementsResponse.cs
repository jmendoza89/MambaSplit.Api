namespace MambaSplit.Api.Contracts;

/// <summary>
/// Response containing a list of settlements for a group.
/// </summary>
public class ListSettlementsResponse
{
    /// <summary>
    /// List of settlements in the group.
    /// </summary>
    public List<SettlementDetailsResponse> Settlements { get; set; } = new();
}
