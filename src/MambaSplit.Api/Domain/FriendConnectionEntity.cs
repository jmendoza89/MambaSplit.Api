using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("friend_connections")]
public class FriendConnectionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("owner_user_id")]
    public Guid OwnerUserId { get; set; }

    [Column("friend_user_id")]
    public Guid? FriendUserId { get; set; }

    [Column("display_name")]
    [MaxLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    [Column("normalized_email")]
    [MaxLength(320)]
    public string NormalizedEmail { get; set; } = string.Empty;

    [Column("original_email")]
    [MaxLength(320)]
    public string OriginalEmail { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    [Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Column("connected_at_utc")]
    public DateTimeOffset? ConnectedAtUtc { get; set; }

    [Column("last_used_at_utc")]
    public DateTimeOffset? LastUsedAtUtc { get; set; }
}
