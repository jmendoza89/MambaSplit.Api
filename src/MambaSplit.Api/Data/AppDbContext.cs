using MambaSplit.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<GroupEntity> Groups => Set<GroupEntity>();
    public DbSet<GroupMemberEntity> GroupMembers => Set<GroupMemberEntity>();
    public DbSet<InviteEntity> Invites => Set<InviteEntity>();
    public DbSet<ExpenseEntity> Expenses => Set<ExpenseEntity>();
    public DbSet<ExpenseSplitEntity> ExpenseSplits => Set<ExpenseSplitEntity>();
    public DbSet<SettlementEntity> Settlements => Set<SettlementEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMemberEntity>()
            .HasIndex(x => new { x.GroupId, x.UserId })
            .IsUnique();

        modelBuilder.Entity<InviteEntity>()
            .HasIndex(x => x.TokenHash)
            .IsUnique();

        modelBuilder.Entity<ExpenseSplitEntity>()
            .HasIndex(x => new { x.ExpenseId, x.UserId })
            .IsUnique();

        modelBuilder.Entity<ExpenseSplitEntity>()
            .HasOne<ExpenseEntity>()
            .WithMany()
            .HasForeignKey(x => x.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExpenseEntity>()
            .HasIndex(x => x.ReversalOfExpenseId)
            .IsUnique();

        modelBuilder.Entity<ExpenseEntity>()
            .HasIndex(x => new { x.GroupId, x.CreatedByUserId, x.IdempotencyKey })
            .IsUnique();

        modelBuilder.Entity<RefreshTokenEntity>()
            .HasIndex(x => x.TokenHash)
            .IsUnique();

        modelBuilder.Entity<GroupMemberEntity>()
            .Property(x => x.Role)
            .HasConversion<string>();
    }
}
