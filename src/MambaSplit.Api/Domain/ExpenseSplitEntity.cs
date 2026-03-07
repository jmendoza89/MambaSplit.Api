using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("expense_splits")]
public class ExpenseSplitEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("expense_id")]
    public Guid ExpenseId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("amount_owed_cents")]
    public long AmountOwedCents { get; set; }
}
