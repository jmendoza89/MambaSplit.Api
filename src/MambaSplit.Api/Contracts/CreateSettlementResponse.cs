namespace MambaSplit.Api.Contracts;

/// <summary>
/// Response after successfully creating a settlement.
/// </summary>
public class CreateSettlementResponse
{
    /// <summary>
    /// The ID of the newly created settlement.
    /// </summary>
    public string SettlementId { get; set; } = string.Empty;
}
