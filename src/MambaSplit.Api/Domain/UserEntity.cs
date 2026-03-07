using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MambaSplit.Api.Domain;

[Table("users")]
public class UserEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("email")]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Column("password_hash")]
    [MaxLength(200)]
    public string PasswordHash { get; set; } = string.Empty;

    [Column("google_sub")]
    [MaxLength(255)]
    public string? GoogleSub { get; set; }

    [Column("display_name")]
    [MaxLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
