using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("invites")]
public class InviteEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("group_id")]
    public Guid GroupId { get; set; }

    [Column("sent_by_user_id")]
    public Guid SentByUserId { get; set; }

    [Column("email")]
    [MaxLength(320)]
    public string SentToEmail { get; set; } = string.Empty;

    [NotMapped]
    public string Email
    {
        get => SentToEmail;
        set => SentToEmail = value;
    }

    [Column("token_hash")]
    [MaxLength(120)]
    public string TokenHash { get; set; } = string.Empty;

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
