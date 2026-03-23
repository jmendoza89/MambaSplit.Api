using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Services;

public class GroupMembershipService
{
    private readonly AppDbContext _db;

    public GroupMembershipService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Inserts a new membership and rebalances all unsettled expenses equally across
    /// the updated member set. Must be called within an active database transaction
    /// (owned by the caller). If the user is already a member, returns immediately
    /// without modification.
    /// </summary>
    public async Task AddMemberAndRebalanceAsync(
        Guid groupId,
        Guid userId,
        Role role,
        CancellationToken ct = default)
    {
        var existing = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);

        if (existing is not null)
        {
            return;
        }

        _db.GroupMembers.Add(new GroupMemberEntity
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);

        var activeMemberIds = await _db.GroupMembers
            .Where(m => m.GroupId == groupId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        var eligibleExpenses = await _db.Expenses
            .Where(e => e.GroupId == groupId
                     && e.ReversalOfExpenseId == null
                     && !_db.Expenses.Any(r => r.ReversalOfExpenseId == e.Id)
                     && !_db.SettlementExpenses.Any(se => se.ExpenseId == e.Id))
            .ToListAsync(ct);

        if (eligibleExpenses.Count == 0)
        {
            return;
        }

        var expenseIds = eligibleExpenses.Select(e => e.Id).ToList();
        var oldSplits = await _db.ExpenseSplits
            .Where(s => expenseIds.Contains(s.ExpenseId))
            .ToListAsync(ct);

        _db.ExpenseSplits.RemoveRange(oldSplits);

        foreach (var expense in eligibleExpenses)
        {
            var newSplits = EqualSplitCalculator.Compute(expense.AmountCents, activeMemberIds);
            foreach (var (uid, owed) in newSplits)
            {
                _db.ExpenseSplits.Add(new ExpenseSplitEntity
                {
                    Id = Guid.NewGuid(),
                    ExpenseId = expense.Id,
                    UserId = uid,
                    AmountOwedCents = owed,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a membership and rebalances all unsettled expenses equally across the
    /// remaining active members. Must be called within an active database transaction
    /// (owned by the caller).
    /// </summary>
    public async Task RemoveMemberAndRebalanceAsync(
        Guid groupId,
        Guid userId,
        CancellationToken ct = default)
    {
        var membership = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);

        if (membership is null)
        {
            throw new ResourceNotFoundException("GroupMember", userId.ToString());
        }

        if (membership.Role == Role.OWNER)
        {
            throw new ValidationException("Group owner cannot leave the group");
        }

        var remainingMemberIds = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.UserId != userId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        if (remainingMemberIds.Count == 0)
        {
            throw new ValidationException("Cannot leave the group as the last remaining member");
        }

        _db.GroupMembers.Remove(membership);
        await _db.SaveChangesAsync(ct);

        var eligibleExpenses = await _db.Expenses
            .Where(e => e.GroupId == groupId
                     && e.ReversalOfExpenseId == null
                     && !_db.Expenses.Any(r => r.ReversalOfExpenseId == e.Id)
                     && !_db.SettlementExpenses.Any(se => se.ExpenseId == e.Id))
            .ToListAsync(ct);

        if (eligibleExpenses.Count == 0)
        {
            return;
        }

        var expenseIds = eligibleExpenses.Select(e => e.Id).ToList();
        var oldSplits = await _db.ExpenseSplits
            .Where(s => expenseIds.Contains(s.ExpenseId))
            .ToListAsync(ct);

        _db.ExpenseSplits.RemoveRange(oldSplits);

        foreach (var expense in eligibleExpenses)
        {
            var newSplits = EqualSplitCalculator.Compute(expense.AmountCents, remainingMemberIds);
            foreach (var (uid, owed) in newSplits)
            {
                _db.ExpenseSplits.Add(new ExpenseSplitEntity
                {
                    Id = Guid.NewGuid(),
                    ExpenseId = expense.Id,
                    UserId = uid,
                    AmountOwedCents = owed,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}