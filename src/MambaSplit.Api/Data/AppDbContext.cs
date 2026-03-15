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
    public DbSet<SettlementExpenseEntity> SettlementExpenses => Set<SettlementExpenseEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMemberEntity>()
            .HasIndex(x => new { x.GroupId, x.UserId })
            .IsUnique();

        modelBuilder.Entity<GroupMemberEntity>()
            .HasOne<GroupEntity>()
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GroupMemberEntity>()
            .HasOne<UserEntity>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<InviteEntity>()
            .HasIndex(x => x.TokenHash)
            .IsUnique();

        modelBuilder.Entity<InviteEntity>()
            .HasIndex(x => new { x.GroupId, x.SentByUserId, x.CreatedAt });

        modelBuilder.Entity<InviteEntity>()
            .HasOne<UserEntity>()
            .WithMany()
            .HasForeignKey(x => x.SentByUserId)
            .OnDelete(DeleteBehavior.Cascade);

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

        modelBuilder.Entity<SettlementExpenseEntity>()
            .HasIndex(x => x.ExpenseId)
            .IsUnique();

        modelBuilder.Entity<SettlementExpenseEntity>()
            .HasOne<SettlementEntity>()
            .WithMany()
            .HasForeignKey(x => x.SettlementId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SettlementExpenseEntity>()
            .HasOne<ExpenseEntity>()
            .WithMany()
            .HasForeignKey(x => x.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RefreshTokenEntity>()
            .HasIndex(x => x.TokenHash)
            .IsUnique();

        modelBuilder.Entity<GroupMemberEntity>()
            .Property(x => x.Role)
            .HasConversion<string>();
    }
}
