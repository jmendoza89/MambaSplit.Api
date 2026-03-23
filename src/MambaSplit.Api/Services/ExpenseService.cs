using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MambaSplit.Api.Services;

public class ExpenseService
{
    private readonly AppDbContext _db;
    private readonly GroupService _groupService;

    public ExpenseService(AppDbContext db, GroupService groupService)
    {
        _db = db;
        _groupService = groupService;
    }

    public async Task<Guid> CreateEqualSplitExpenseAsync(
        Guid groupId,
        Guid actorUserId,
        Guid payerUserId,
        string description,
        long totalAmountCents,
        List<Guid> participants,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        if (participants.Count == 0)
        {
            throw new ValidationException("Participants list cannot be empty");
        }

        if (totalAmountCents <= 0)
        {
            throw new ValidationException("Amount must be greater than 0");
        }

        if (participants.Distinct().Count() != participants.Count)
        {
            throw new ValidationException("Duplicate users in participants list");
        }

        var memberIds = participants.Append(payerUserId).Distinct();
        await _groupService.RequireMembersAsync(groupId, memberIds, ct);
        EnforceDelegatedPayerPolicy(actorUserId, payerUserId);

        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        var idempotencyHash = BuildEqualIdempotencyHash(description, totalAmountCents, payerUserId, participants);
        var existingExpenseId = await ResolveIdempotentDuplicateAsync(
            groupId,
            actorUserId,
            normalizedIdempotencyKey,
            idempotencyHash,
            ct);
        if (existingExpenseId is not null)
        {
            return existingExpenseId.Value;
        }

        var expenseId = Guid.NewGuid();
        _db.Expenses.Add(new ExpenseEntity
        {
            Id = expenseId,
            GroupId = groupId,
            PayerUserId = payerUserId,
            CreatedByUserId = actorUserId,
            Description = description,
            AmountCents = totalAmountCents,
            IdempotencyKey = normalizedIdempotencyKey,
            IdempotencyHash = idempotencyHash,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var splits = EqualSplitCalculator.Compute(totalAmountCents, participants);
        foreach (var (uid, owed) in splits)
        {
            _db.ExpenseSplits.Add(new ExpenseSplitEntity
            {
                Id = Guid.NewGuid(),
                ExpenseId = expenseId,
                UserId = uid,
                AmountOwedCents = owed,
            });
        }

        await _db.SaveChangesAsync(ct);
        return expenseId;
    }

    public async Task<Guid> CreateExactSplitExpenseAsync(
        Guid groupId,
        Guid actorUserId,
        Guid payerUserId,
        string description,
        long totalAmountCents,
        List<SplitExactItem> items,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            throw new ValidationException("Split items list cannot be empty");
        }

        if (totalAmountCents <= 0)
        {
            throw new ValidationException("Amount must be greater than 0");
        }

        long sum;
        try
        {
            sum = items.Sum(x => x.AmountCents);
        }
        catch (OverflowException)
        {
            throw new ValidationException("Split sum overflow");
        }

        if (sum != totalAmountCents)
        {
            throw new ValidationException($"Split sum ({sum}) must equal total amount ({totalAmountCents})");
        }

        var memberIds = items.Select(i => i.UserId).Append(payerUserId).Distinct();
        await _groupService.RequireMembersAsync(groupId, memberIds, ct);
        EnforceDelegatedPayerPolicy(actorUserId, payerUserId);

        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        var idempotencyHash = BuildExactIdempotencyHash(description, totalAmountCents, payerUserId, items);
        var existingExpenseId = await ResolveIdempotentDuplicateAsync(
            groupId,
            actorUserId,
            normalizedIdempotencyKey,
            idempotencyHash,
            ct);
        if (existingExpenseId is not null)
        {
            return existingExpenseId.Value;
        }

        var expenseId = Guid.NewGuid();
        _db.Expenses.Add(new ExpenseEntity
        {
            Id = expenseId,
            GroupId = groupId,
            PayerUserId = payerUserId,
            CreatedByUserId = actorUserId,
            Description = description,
            AmountCents = totalAmountCents,
            IdempotencyKey = normalizedIdempotencyKey,
            IdempotencyHash = idempotencyHash,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (!seen.Add(item.UserId))
            {
                throw new ValidationException("Duplicate user in split items");
            }

            if (item.AmountCents < 0)
            {
                throw new ValidationException("Split amount cannot be negative");
            }

            _db.ExpenseSplits.Add(new ExpenseSplitEntity
            {
                Id = Guid.NewGuid(),
                ExpenseId = expenseId,
                UserId = item.UserId,
                AmountOwedCents = item.AmountCents,
            });
        }

        await _db.SaveChangesAsync(ct);
        return expenseId;
    }

    public async Task DeleteExpenseAsync(Guid groupId, Guid expenseId, Guid actorUserId, CancellationToken ct = default)
    {
        var expense = await _db.Expenses
            .FirstOrDefaultAsync(e => e.Id == expenseId && e.GroupId == groupId, ct);
        if (expense is null)
        {
            throw new ResourceNotFoundException("Expense", expenseId.ToString());
        }

        if (expense.PayerUserId != actorUserId)
        {
            throw new AuthorizationException("delete", "expense " + expenseId);
        }

        if (expense.ReversalOfExpenseId is not null)
        {
            throw new ValidationException("Reversal entries cannot be deleted");
        }

        var existingReversal = await _db.Expenses
            .AnyAsync(e => e.ReversalOfExpenseId == expense.Id, ct);
        if (existingReversal)
        {
            throw new ConflictException("Expense already reversed");
        }

        var isSettlementLinked = await _db.SettlementExpenses
            .AnyAsync(se => se.ExpenseId == expenseId, ct);
        if (isSettlementLinked)
        {
            throw new ConflictException("Expense is settled and cannot be deleted");
        }

        var splits = await _db.ExpenseSplits
            .Where(s => s.ExpenseId == expenseId)
            .ToListAsync(ct);

        var reversalId = Guid.NewGuid();
        _db.Expenses.Add(new ExpenseEntity
        {
            Id = reversalId,
            GroupId = groupId,
            PayerUserId = expense.PayerUserId,
            CreatedByUserId = actorUserId,
            Description = BuildReversalDescription(expense.Description),
            AmountCents = checked(-expense.AmountCents),
            ReversalOfExpenseId = expense.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var split in splits)
        {
            _db.ExpenseSplits.Add(new ExpenseSplitEntity
            {
                Id = Guid.NewGuid(),
                ExpenseId = reversalId,
                UserId = split.UserId,
                AmountOwedCents = checked(-split.AmountOwedCents),
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsReversalConflict(ex))
        {
            throw new ConflictException("Expense already reversed");
        }
        catch (DbUpdateException ex) when (IsSettlementLinkConflict(ex))
        {
            throw new ConflictException("Expense is settled and cannot be deleted");
        }
    }

    private static void EnforceDelegatedPayerPolicy(Guid actorUserId, Guid payerUserId)
    {
        // Explicit policy (current): any group member can record on behalf of another member.
        _ = actorUserId;
        _ = payerUserId;
    }

    private async Task<Guid?> ResolveIdempotentDuplicateAsync(
        Guid groupId,
        Guid actorUserId,
        string? idempotencyKey,
        string idempotencyHash,
        CancellationToken ct)
    {
        if (idempotencyKey is null)
        {
            return null;
        }

        var existing = await _db.Expenses
            .FirstOrDefaultAsync(
                e => e.GroupId == groupId
                     && e.CreatedByUserId == actorUserId
                     && e.IdempotencyKey == idempotencyKey,
                ct);
        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(existing.IdempotencyHash, idempotencyHash, StringComparison.Ordinal))
        {
            throw new ConflictException("Idempotency key already used with a different payload");
        }

        return existing.Id;
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        if (idempotencyKey is null)
        {
            return null;
        }

        var normalized = idempotencyKey.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > 120)
        {
            throw new ValidationException("Idempotency key too long");
        }

        return normalized;
    }

    private static string BuildEqualIdempotencyHash(string description, long totalAmountCents, Guid payerUserId, List<Guid> participants)
    {
        var sortedParticipants = participants.OrderBy(x => x.ToString()).Select(x => x.ToString());
        var fingerprint = string.Join("|", new[]
        {
            "equal",
            description.Trim(),
            totalAmountCents.ToString(),
            payerUserId.ToString(),
            string.Join(",", sortedParticipants),
        });
        return HashFingerprint(fingerprint);
    }

    private static string BuildExactIdempotencyHash(string description, long totalAmountCents, Guid payerUserId, List<SplitExactItem> items)
    {
        var orderedItems = items
            .OrderBy(i => i.UserId.ToString())
            .Select(i => $"{i.UserId}:{i.AmountCents}");
        var fingerprint = string.Join("|", new[]
        {
            "exact",
            description.Trim(),
            totalAmountCents.ToString(),
            payerUserId.ToString(),
            string.Join(",", orderedItems),
        });
        return HashFingerprint(fingerprint);
    }

    private static string HashFingerprint(string value)
    {
        return Security.TokenCodec.Sha256Base64Url(value);
    }

    private static string BuildReversalDescription(string originalDescription)
    {
        const string prefix = "Reversal: ";
        var remaining = 300 - prefix.Length;
        var trimmedOriginal = originalDescription.Length <= remaining
            ? originalDescription
            : originalDescription[..remaining];
        return prefix + trimmedOriginal;
    }

    private static bool IsReversalConflict(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException pg)
        {
            return false;
        }

        return pg.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(pg.ConstraintName, "ix_expenses_reversal_of_expense_id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSettlementLinkConflict(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException pg)
        {
            return false;
        }

        return pg.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
               string.Equals(pg.ConstraintName, "fk_settlement_expenses_expenses_expense_id", StringComparison.OrdinalIgnoreCase);
    }
}

public record SplitExactItem(Guid UserId, long AmountCents);
