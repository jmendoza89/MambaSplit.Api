using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MambaSplit.Api.Services;

public class SettlementService
{
    private readonly AppDbContext _db;
    private readonly GroupService _groupService;

    public SettlementService(AppDbContext db, GroupService groupService)
    {
        _db = db;
        _groupService = groupService;
    }

    public async Task<SettlementDetails> CreateSettlementAsync(
        Guid groupId,
        Guid actorUserId,
        Guid fromUserId,
        Guid toUserId,
        long amountCents,
        List<Guid> expenseIds,
        string? note = null,
        DateTimeOffset? settledAt = null,
        CancellationToken ct = default)
    {
        var normalizedExpenseIds = expenseIds
            .Where(id => id != Guid.Empty)
            .ToList();

        if (amountCents <= 0)
        {
            throw new ValidationException("Amount must be greater than 0");
        }

        if (fromUserId == toUserId)
        {
            throw new ValidationException("From and to users cannot be the same");
        }

        if (!string.IsNullOrWhiteSpace(note) && note.Length > 500)
        {
            throw new ValidationException("Settlement note cannot exceed 500 characters");
        }

        if (normalizedExpenseIds.Distinct().Count() != normalizedExpenseIds.Count)
        {
            throw new ValidationException("Duplicate expense ids in settlement payload");
        }

        if (normalizedExpenseIds.Count == 0)
        {
            throw new ValidationException("At least one expense must be selected");
        }

        await _groupService.RequireMemberAsync(groupId, actorUserId, ct);
        await _groupService.RequireMembersAsync(groupId, new[] { fromUserId, toUserId }, ct);

        EnforceSettlementAuthorPolicy(actorUserId, fromUserId);

        var effectiveSettAtInput = settledAt ?? DateTimeOffset.UtcNow;
        var effectiveSettledAt = effectiveSettAtInput.ToUniversalTime();
        var now = DateTimeOffset.UtcNow;
        if (effectiveSettledAt > now.AddMinutes(5))
        {
            throw new ValidationException("Settlement date cannot be in the future");
        }

        var settlement = new SettlementEntity
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            FromUserId = fromUserId,
            ToUserId = toUserId,
            AmountCents = amountCents,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = effectiveSettledAt,
        };

        _db.Settlements.Add(settlement);

        if (normalizedExpenseIds.Count > 0)
        {
            var expenses = await _db.Expenses
                .Where(e => e.GroupId == groupId && normalizedExpenseIds.Contains(e.Id))
                .Select(e => new { e.Id, e.PayerUserId })
                .ToListAsync(ct);
            if (expenses.Count != normalizedExpenseIds.Count)
            {
                throw new ValidationException("One or more expenses do not belong to this group");
            }

            var expenseIdsSet = normalizedExpenseIds.ToHashSet();
            var splits = await _db.ExpenseSplits
                .Where(s => expenseIdsSet.Contains(s.ExpenseId) && (s.UserId == fromUserId || s.UserId == toUserId))
                .Select(s => new { s.ExpenseId, s.UserId, s.AmountOwedCents })
                .ToListAsync(ct);
            var splitsByExpense = splits
                .GroupBy(s => s.ExpenseId)
                .ToDictionary(g => g.Key, g => g.ToList());

            long expectedAmountCents = 0;
            try
            {
                foreach (var expense in expenses)
                {
                    var expenseSplits = splitsByExpense.GetValueOrDefault(expense.Id, []);
                    if (expense.PayerUserId == toUserId)
                    {
                        var fromOwed = expenseSplits
                            .Where(s => s.UserId == fromUserId)
                            .Sum(s => s.AmountOwedCents);
                        expectedAmountCents = checked(expectedAmountCents + fromOwed);
                    }

                    if (expense.PayerUserId == fromUserId)
                    {
                        var toOwed = expenseSplits
                            .Where(s => s.UserId == toUserId)
                            .Sum(s => s.AmountOwedCents);
                        expectedAmountCents = checked(expectedAmountCents - toOwed);
                    }
                }
            }
            catch (OverflowException)
            {
                throw new ValidationException("Settlement amount calculation overflow");
            }

            if (expectedAmountCents <= 0)
            {
                throw new ValidationException("Selected expenses do not produce an outstanding balance for the selected payer and receiver");
            }

            if (expectedAmountCents != amountCents)
            {
                throw new ValidationException($"Settlement amount ({amountCents}) must match selected outstanding balance ({expectedAmountCents})");
            }

            var alreadySettled = await _db.SettlementExpenses
                .Where(se => normalizedExpenseIds.Contains(se.ExpenseId))
                .Select(se => se.ExpenseId)
                .Distinct()
                .ToListAsync(ct);
            if (alreadySettled.Count > 0)
            {
                throw new ConflictException("One or more expenses are already associated with a settlement");
            }

            foreach (var expenseId in normalizedExpenseIds)
            {
                _db.SettlementExpenses.Add(new SettlementExpenseEntity
                {
                    Id = Guid.NewGuid(),
                    SettlementId = settlement.Id,
                    ExpenseId = expenseId,
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsExpenseSettlementLinkConflict(ex))
        {
            throw new ConflictException("One or more expenses are already associated with a settlement");
        }
        catch (DbUpdateException ex) when (IsSettlementIntegrityConflict(ex))
        {
            throw new ConflictException("Settlement conflicts with current group or expense state");
        }

        return await BuildSettlementDetailsResponseAsync(settlement, ct);
    }

    public async Task<SettlementDetails> GetSettlementAsync(Guid settlementId, Guid actorUserId, CancellationToken ct = default)
    {
        var settlement = await _db.Settlements.FindAsync(new object[] { settlementId }, ct);
        if (settlement is null)
        {
            throw new ResourceNotFoundException("Settlement", settlementId.ToString());
        }

        await _groupService.RequireMemberAsync(settlement.GroupId, actorUserId, ct);
        return await BuildSettlementDetailsResponseAsync(settlement, ct);
    }

    public async Task<ListSettlementsResult> ListGroupSettlementsAsync(
        Guid groupId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        await _groupService.RequireMemberAsync(groupId, actorUserId, ct);

        var settlements = await _db.Settlements
            .Where(s => s.GroupId == groupId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var details = await MapSettlementsAsync(settlements, ct);
        return new ListSettlementsResult(details);
    }

    public async Task<ListSettlementsResult> ListUserSettlementsAsync(
        Guid userId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        if (userId != actorUserId)
        {
            throw new AuthorizationException("list settlements for another user");
        }

        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        var groupIds = await _db.GroupMembers
            .Where(gm => gm.UserId == userId)
            .Select(gm => gm.GroupId)
            .Distinct()
            .ToListAsync(ct);

        if (groupIds.Count == 0)
        {
            return new ListSettlementsResult([]);
        }

        var settlements = await _db.Settlements
            .Where(s => groupIds.Contains(s.GroupId) && (s.FromUserId == userId || s.ToUserId == userId))
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var details = await MapSettlementsAsync(settlements, ct);
        return new ListSettlementsResult(details);
    }

    public async Task<AdminSettlementResetResult> ResetGroupSettlementsAsync(
        Guid groupId,
        CancellationToken ct = default)
    {
        var groupExists = await _db.Groups.AnyAsync(g => g.Id == groupId, ct);
        if (!groupExists)
        {
            throw new ResourceNotFoundException("Group", groupId.ToString());
        }

        var settlements = await _db.Settlements
            .Where(s => s.GroupId == groupId)
            .ToListAsync(ct);
        if (settlements.Count == 0)
        {
            return new AdminSettlementResetResult(groupId, 0, 0);
        }

        var settlementIds = settlements.Select(s => s.Id).ToList();
        var linkedExpenseCount = await _db.SettlementExpenses
            .Where(se => settlementIds.Contains(se.SettlementId))
            .Select(se => se.ExpenseId)
            .Distinct()
            .CountAsync(ct);

        _db.Settlements.RemoveRange(settlements);
        await _db.SaveChangesAsync(ct);

        return new AdminSettlementResetResult(groupId, settlements.Count, linkedExpenseCount);
    }

    private async Task<List<SettlementDetails>> MapSettlementsAsync(
        List<SettlementEntity> settlements,
        CancellationToken ct)
    {
        var settlementIds = settlements.Select(s => s.Id).ToList();
        var linksBySettlementId = settlementIds.Count == 0
            ? new Dictionary<Guid, List<Guid>>()
            : await _db.SettlementExpenses
                .Where(se => settlementIds.Contains(se.SettlementId))
                .GroupBy(se => se.SettlementId)
                .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.ExpenseId).ToList(), ct);

        var userIds = settlements
            .SelectMany(s => new[] { s.FromUserId, s.ToUserId })
            .Distinct()
            .ToList();

        var usersById = userIds.Count == 0
            ? new Dictionary<Guid, UserEntity>()
            : await _db.Users
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, ct);

        var details = new List<SettlementDetails>();
        foreach (var settlement in settlements)
        {
            if (usersById.TryGetValue(settlement.FromUserId, out var fromUser) &&
                usersById.TryGetValue(settlement.ToUserId, out var toUser))
            {
                details.Add(MapToSettlementDetails(
                    settlement,
                    fromUser,
                    toUser,
                    linksBySettlementId.GetValueOrDefault(settlement.Id, [])));
            }
        }

        return details;
    }

    private async Task<SettlementDetails> BuildSettlementDetailsResponseAsync(
        SettlementEntity settlement,
        CancellationToken ct)
    {
        var expenseIds = await _db.SettlementExpenses
            .Where(se => se.SettlementId == settlement.Id)
            .Select(se => se.ExpenseId)
            .ToListAsync(ct);

        var fromUser = await _db.Users.FindAsync(new object[] { settlement.FromUserId }, ct);
        var toUser = await _db.Users.FindAsync(new object[] { settlement.ToUserId }, ct);

        if (fromUser is null || toUser is null)
        {
            throw new ResourceNotFoundException("Settlement", settlement.Id.ToString());
        }

        return MapToSettlementDetails(settlement, fromUser, toUser, expenseIds);
    }

    private static SettlementDetails MapToSettlementDetails(
        SettlementEntity settlement,
        UserEntity fromUser,
        UserEntity toUser,
        List<Guid> expenseIds)
    {
        return new SettlementDetails(
            settlement.Id,
            settlement.GroupId,
            settlement.FromUserId,
            fromUser.DisplayName,
            settlement.ToUserId,
            toUser.DisplayName,
            settlement.AmountCents,
            settlement.Note,
            settlement.CreatedAt,
            expenseIds);
    }

    private static void EnforceSettlementAuthorPolicy(Guid actorUserId, Guid fromUserId)
    {
        if (actorUserId != fromUserId)
        {
            throw new AuthorizationException("Not authorized to create settlement for another member");
        }
    }

    private static bool IsExpenseSettlementLinkConflict(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException pg)
        {
            return false;
        }

        return pg.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(pg.ConstraintName, "ix_settlement_expenses_expense_id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSettlementIntegrityConflict(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException pg)
        {
            return false;
        }

        if (pg.SqlState != PostgresErrorCodes.ForeignKeyViolation &&
            pg.SqlState != PostgresErrorCodes.UniqueViolation &&
            pg.SqlState != PostgresErrorCodes.CheckViolation)
        {
            return false;
        }

        var constraint = pg.ConstraintName ?? string.Empty;
        return constraint.StartsWith("fk_settlement_", StringComparison.OrdinalIgnoreCase) ||
               constraint.StartsWith("fk_settlements_", StringComparison.OrdinalIgnoreCase) ||
               constraint.StartsWith("ix_settlement_", StringComparison.OrdinalIgnoreCase);
    }

    public record ListSettlementsResult(List<SettlementDetails> Settlements);
    public record AdminSettlementResetResult(Guid GroupId, int DeletedSettlementCount, int ReleasedExpenseCount);

    public record SettlementDetails(
        Guid Id,
        Guid GroupId,
        Guid FromUserId,
        string FromUserName,
        Guid ToUserId,
        string ToUserName,
        long AmountCents,
        string? Note,
        DateTimeOffset SettledAt,
        List<Guid> ExpenseIds);
}
