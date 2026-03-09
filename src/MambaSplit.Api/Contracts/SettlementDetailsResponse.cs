namespace MambaSplit.Api.Contracts;

/// <summary>
/// Details of a settlement (payment between users).
/// </summary>
public class SettlementDetailsResponse
{
    /// <summary>
    /// Unique settlement ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// User ID of the person making the payment.
    /// </summary>
    public string FromUserId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the person making the payment.
    /// </summary>
    public string FromUserName { get; set; } = string.Empty;

    /// <summary>
    /// User ID of the person receiving the payment.
    /// </summary>
    public string ToUserId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the person receiving the payment.
    /// </summary>
    public string ToUserName { get; set; } = string.Empty;

    /// <summary>
    /// Amount in cents (e.g., $53.00 = 5300).
    /// </summary>
    public long AmountCents { get; set; }

    /// <summary>
    /// Optional note about the settlement.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Date and time when the settlement was created (ISO 8601 format).
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;
}
