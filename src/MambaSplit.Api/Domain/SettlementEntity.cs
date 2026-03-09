using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("settlements")]
public class SettlementEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("group_id")]
    public Guid GroupId { get; set; }

    [Column("from_user_id")]
    public Guid FromUserId { get; set; }

    [Column("to_user_id")]
    public Guid ToUserId { get; set; }

    [Column("amount_cents")]
    public long AmountCents { get; set; }

    [Column("note")]
    [MaxLength(500)]
    public string? Note { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
