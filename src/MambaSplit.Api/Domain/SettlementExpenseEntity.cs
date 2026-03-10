using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("settlement_expenses")]
public class SettlementExpenseEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("settlement_id")]
    public Guid SettlementId { get; set; }

    [Column("expense_id")]
    public Guid ExpenseId { get; set; }
}
