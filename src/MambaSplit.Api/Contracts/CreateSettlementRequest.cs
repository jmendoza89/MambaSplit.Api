namespace MambaSplit.Api.Contracts;

/// <summary>
/// Request to create a settlement (payment between users).
/// </summary>
public class CreateSettlementRequest
{
    /// <summary>
    /// User ID of the person making the payment.
    /// </summary>
    public string FromUserId { get; set; } = string.Empty;

    /// <summary>
    /// User ID of the person receiving the payment.
    /// </summary>
    public string ToUserId { get; set; } = string.Empty;

    /// <summary>
    /// Amount in cents (e.g., $53.00 = 5300).
    /// </summary>
    public long AmountCents { get; set; }

    /// <summary>
    /// Optional note about the settlement (max 500 characters).
    /// </summary>
    public string? Note { get; set; }
}
