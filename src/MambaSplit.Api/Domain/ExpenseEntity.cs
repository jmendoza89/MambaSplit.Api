using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("expenses")]
public class ExpenseEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("group_id")]
    public Guid GroupId { get; set; }

    [Column("payer_user_id")]
    public Guid PayerUserId { get; set; }

    [Column("created_by_user_id")]
    public Guid CreatedByUserId { get; set; }

    [Column("description")]
    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;

    [Column("amount_cents")]
    public long AmountCents { get; set; }

    [Column("reversal_of_expense_id")]
    public Guid? ReversalOfExpenseId { get; set; }

    [Column("idempotency_key")]
    [MaxLength(120)]
    public string? IdempotencyKey { get; set; }

    [Column("idempotency_hash")]
    [MaxLength(120)]
    public string? IdempotencyHash { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
