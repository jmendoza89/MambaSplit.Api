using System.Globalization;
using System.Text.Json.Nodes;
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
    private readonly TransactionalEmailService _transactionalEmailService;
    private readonly ILogger<SettlementService> _logger;

    public SettlementService(
        AppDbContext db,
        GroupService groupService,
        TransactionalEmailService transactionalEmailService,
        ILogger<SettlementService> logger)
    {
        _db = db;
        _groupService = groupService;
        _transactionalEmailService = transactionalEmailService;
        _logger = logger;
    }

    public async Task<SettlementDetails> CreateSettlementAsync(
        Guid groupId,
        Guid actorUserId,
        Guid fromUserId,
        Guid toUserId,
        long amountCents,
        string? note = null,
        DateTimeOffset? settledAt = null,
        CancellationToken ct = default)
    {
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

        // Auto-select all unsettled expenses that are pair-relevant:
        // expenses paid by toUserId where fromUserId has a split (fromUser owes toUser),
        // and expenses paid by fromUserId where toUserId has a split (netted off in the opposite direction).
        var groupExpenseIds = _db.Expenses
            .Where(e => e.GroupId == groupId)
            .Select(e => e.Id);
        var alreadyLinkedExpenseIds = (await _db.SettlementExpenses
            .Where(se => groupExpenseIds.Contains(se.ExpenseId))
            .Select(se => se.ExpenseId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        var candidateExpenses = await _db.Expenses
            .Where(e => e.GroupId == groupId
                && !alreadyLinkedExpenseIds.Contains(e.Id)
                && (e.PayerUserId == toUserId || e.PayerUserId == fromUserId))
            .Select(e => new { e.Id, e.PayerUserId })
            .ToListAsync(ct);

        var candidateIds = candidateExpenses.Select(e => e.Id).ToHashSet();
        var splits = await _db.ExpenseSplits
            .Where(s => candidateIds.Contains(s.ExpenseId) && (s.UserId == fromUserId || s.UserId == toUserId))
            .Select(s => new { s.ExpenseId, s.UserId, s.AmountOwedCents })
            .ToListAsync(ct);

        var splitsByExpense = splits
            .GroupBy(s => s.ExpenseId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Only include expenses that actually have a non-zero split for this pair.
        var pairExpenses = candidateExpenses
            .Where(e =>
            {
                var expSplits = splitsByExpense.GetValueOrDefault(e.Id, []);
                if (e.PayerUserId == toUserId)
                    return expSplits.Any(s => s.UserId == fromUserId && s.AmountOwedCents > 0);
                if (e.PayerUserId == fromUserId)
                    return expSplits.Any(s => s.UserId == toUserId && s.AmountOwedCents > 0);
                return false;
            })
            .ToList();

        long expectedAmountCents = 0;
        try
        {
            foreach (var expense in pairExpenses)
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
            throw new ValidationException("No outstanding balance exists for this pair");
        }

        if (expectedAmountCents != amountCents)
        {
            throw new ValidationException($"Settlement amount ({amountCents}) does not match outstanding pair balance ({expectedAmountCents})");
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

        foreach (var expense in pairExpenses)
        {
            _db.SettlementExpenses.Add(new SettlementExpenseEntity
            {
                Id = Guid.NewGuid(),
                SettlementId = settlement.Id,
                ExpenseId = expense.Id,
            });
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

        var details = await BuildSettlementDetailsResponseAsync(settlement, ct);
        await SendSettlementEmailAsync(details, actorUserId, ct);
        return details;
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

    private async Task SendSettlementEmailAsync(SettlementDetails settlement, Guid actorUserId, CancellationToken ct)
    {
        try
        {
            var group = await _db.Groups.FindAsync(new object[] { settlement.GroupId }, ct);
            if (group is null)
            {
                _logger.LogWarning("Skipping settlement email send because group was not found. settlementId={SettlementId} groupId={GroupId}", settlement.Id, settlement.GroupId);
                return;
            }

            var memberUserIds = await _db.GroupMembers
                .Where(gm => gm.GroupId == settlement.GroupId)
                .Select(gm => gm.UserId)
                .Distinct()
                .ToListAsync(ct);

            var recipientEmails = await _db.Users
                .Where(u => memberUserIds.Contains(u.Id) && !string.IsNullOrWhiteSpace(u.Email))
                .Select(u => u.Email)
                .ToListAsync(ct);

            var recipients = recipientEmails
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(email => email, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (recipients.Count == 0)
            {
                _logger.LogInformation("Skipping settlement email send because no recipients were found. settlementId={SettlementId}", settlement.Id);
                return;
            }

            var model = new JsonObject
            {
                ["groupName"] = group.Name,
                ["groupId"] = settlement.GroupId.ToString(),
                ["payerName"] = settlement.FromUserName,
                ["receiverName"] = settlement.ToUserName,
                ["amountDisplay"] = FormatAmount(settlement.AmountCents),
                ["settledAtDisplay"] = settlement.SettledAt.ToUniversalTime().ToString("MMMM d, yyyy 'at' h:mm tt 'UTC'", CultureInfo.InvariantCulture),
                ["expenseCountText"] = FormatExpenseCount(settlement.ExpenseIds.Count),
                ["noteText"] = string.IsNullOrWhiteSpace(settlement.Note) ? "No note was added." : settlement.Note,
            };

            await _transactionalEmailService.SendTemplateAsync(
                "settlement",
                recipients,
                [],
                [],
                null,
                model,
                ["settlement", "group:" + settlement.GroupId.ToString("N")],
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settlement email send failed for settlementId={SettlementId} groupId={GroupId}", settlement.Id, settlement.GroupId);
        }
    }

    private static string FormatAmount(long amountCents)
    {
        var amount = amountCents / 100m;
        return "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatExpenseCount(int expenseCount)
    {
        return expenseCount == 1 ? "1 linked expense" : $"{expenseCount} linked expenses";
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