using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Services;

public class FriendService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FriendService> _logger;

    public FriendService(AppDbContext db, ILogger<FriendService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Upsert a Pending friend connection when an invite is sent.
    /// Owner = the person sending the invite.
    /// </summary>
    public async Task UpsertOnInviteSentAsync(
        Guid ownerUserId,
        string email,
        string? displayName,
        CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var existing = await _db.FriendConnections
            .FirstOrDefaultAsync(fc =>
                fc.OwnerUserId == ownerUserId && fc.NormalizedEmail == normalizedEmail, ct);

        if (existing is not null)
        {
            // Already exists — don't downgrade a Connected row to Pending.
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.DisplayName = displayName;
            }
            await _db.SaveChangesAsync(ct);
            return;
        }

        var friendUser = await _db.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);

        _db.FriendConnections.Add(new FriendConnectionEntity
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            FriendUserId = friendUser?.Id,
            DisplayName = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : friendUser?.DisplayName ?? email,
            NormalizedEmail = normalizedEmail,
            OriginalEmail = email.Trim(),
            Status = friendUser is not null ? "Connected" : "Pending",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ConnectedAtUtc = friendUser is not null ? DateTimeOffset.UtcNow : null,
            LastUsedAtUtc = friendUser is not null ? DateTimeOffset.UtcNow : null,
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Upsert friend connections in both directions when an invite is accepted.
    /// </summary>
    public async Task UpsertOnInviteAcceptedAsync(
        Guid inviterUserId,
        Guid acceptingUserId,
        CancellationToken ct = default)
    {
        var inviter = await _db.Users.FindAsync(new object[] { inviterUserId }, ct);
        var acceptor = await _db.Users.FindAsync(new object[] { acceptingUserId }, ct);
        if (inviter is null || acceptor is null) return;

        var now = DateTimeOffset.UtcNow;

        // Direction 1: inviter → acceptor
        await UpsertConnectedAsync(
            inviterUserId,
            acceptingUserId,
            acceptor.DisplayName,
            acceptor.Email,
            now,
            ct);

        // Direction 2: acceptor → inviter
        await UpsertConnectedAsync(
            acceptingUserId,
            inviterUserId,
            inviter.DisplayName,
            inviter.Email,
            now,
            ct);
    }

    private async Task UpsertConnectedAsync(
        Guid ownerUserId,
        Guid friendUserId,
        string displayName,
        string email,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var existing = await _db.FriendConnections
            .FirstOrDefaultAsync(fc =>
                fc.OwnerUserId == ownerUserId && fc.NormalizedEmail == normalizedEmail, ct);

        if (existing is not null)
        {
            existing.FriendUserId = friendUserId;
            existing.Status = "Connected";
            existing.ConnectedAtUtc ??= now;
            existing.LastUsedAtUtc = now;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.DisplayName = displayName;
            }
        }
        else
        {
            _db.FriendConnections.Add(new FriendConnectionEntity
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                FriendUserId = friendUserId,
                DisplayName = displayName,
                NormalizedEmail = normalizedEmail,
                OriginalEmail = email.Trim(),
                Status = "Connected",
                CreatedAtUtc = now,
                ConnectedAtUtc = now,
                LastUsedAtUtc = now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// List friends for the owner, pre-sorted: Connected first by last_used_at_utc desc,
    /// then Pending by created_at_utc desc.
    /// </summary>
    public async Task<List<FriendListItem>> ListForUserAsync(
        Guid ownerUserId,
        CancellationToken ct = default)
    {
        var connections = await _db.FriendConnections
            .Where(fc => fc.OwnerUserId == ownerUserId)
            .ToListAsync(ct);

        // Sort: Connected first (by LastUsedAtUtc desc), then Pending (by CreatedAtUtc desc)
        var sorted = connections
            .OrderBy(fc => fc.Status == "Connected" ? 0 : 1)
            .ThenByDescending(fc => fc.Status == "Connected" ? fc.LastUsedAtUtc : fc.CreatedAtUtc)
            .ToList();

        var connectedFriendIds = sorted
            .Where(fc => fc.FriendUserId.HasValue)
            .Select(fc => fc.FriendUserId!.Value)
            .Distinct()
            .ToList();

        var summaries = connectedFriendIds.Count > 0
            ? await ComputeBatchSummariesAsync(ownerUserId, connectedFriendIds, ct)
            : new Dictionary<Guid, (long NetBalanceCents, int SharedGroupCount, bool HasActiveBalances)>();

        var result = new List<FriendListItem>();
        foreach (var fc in sorted)
        {
            var (netBalanceCents, sharedGroupCount, hasActiveBalances) =
                fc.FriendUserId.HasValue && summaries.TryGetValue(fc.FriendUserId.Value, out var summary)
                    ? summary
                    : (0L, 0, false);

            var friendDisplayName = fc.DisplayName;
            var netBalanceLabel = FormatBalanceLabel(netBalanceCents, friendDisplayName);

            result.Add(new FriendListItem(
                fc.Id,
                fc.DisplayName,
                fc.OriginalEmail,
                fc.Status,
                fc.FriendUserId?.ToString(),
                netBalanceCents,
                netBalanceLabel,
                sharedGroupCount,
                hasActiveBalances,
                (fc.LastUsedAtUtc ?? fc.CreatedAtUtc).ToString("O")));
        }

        return result;
    }

    /// <summary>
    /// Get detail for a specific friend connection, including per-group breakdown.
    /// </summary>
    public async Task<FriendDetail> GetDetailAsync(
        Guid ownerUserId,
        Guid friendConnectionId,
        CancellationToken ct = default)
    {
        var fc = await _db.FriendConnections.FindAsync(new object[] { friendConnectionId }, ct);
        if (fc is null || fc.OwnerUserId != ownerUserId)
        {
            throw new ResourceNotFoundException("FriendConnection", friendConnectionId.ToString());
        }

        var sharedGroups = new List<SharedGroupBalance>();
        long netBalanceCents = 0;

        if (fc.FriendUserId.HasValue)
        {
            (sharedGroups, netBalanceCents) = await ComputePerGroupBalancesAsync(
                ownerUserId, fc.FriendUserId.Value, ct);
        }

        var netBalanceLabel = FormatBalanceLabel(netBalanceCents, fc.DisplayName);
        var summary = netBalanceCents == 0
            ? "All settled"
            : netBalanceLabel;

        return new FriendDetail(
            fc.Id,
            fc.DisplayName,
            fc.OriginalEmail,
            fc.Status,
            fc.FriendUserId?.ToString(),
            netBalanceCents,
            netBalanceLabel,
            summary,
            sharedGroups);
    }

    private async Task<Dictionary<Guid, (long NetBalanceCents, int SharedGroupCount, bool HasActiveBalances)>>
        ComputeBatchSummariesAsync(Guid ownerUserId, List<Guid> friendUserIds, CancellationToken ct)
    {
        var ownerGroupIds = await _db.GroupMembers
            .Where(m => m.UserId == ownerUserId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        if (ownerGroupIds.Count == 0)
            return friendUserIds.ToDictionary(id => id, _ => (0L, 0, false));

        var friendMemberships = await _db.GroupMembers
            .Where(m => friendUserIds.Contains(m.UserId) && ownerGroupIds.Contains(m.GroupId))
            .Select(m => new { m.UserId, m.GroupId })
            .ToListAsync(ct);

        var sharedGroupsByFriend = friendMemberships
            .GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).ToList());

        var allSharedGroupIds = friendMemberships
            .Select(m => m.GroupId)
            .Distinct()
            .ToList();

        if (allSharedGroupIds.Count == 0)
            return friendUserIds.ToDictionary(id => id, _ => (0L, 0, false));

        var relevantUserIds = new HashSet<Guid>(friendUserIds) { ownerUserId };

        var expenses = await _db.Expenses
            .Where(e => allSharedGroupIds.Contains(e.GroupId) && relevantUserIds.Contains(e.PayerUserId))
            .Select(e => new { e.Id, e.GroupId, e.PayerUserId })
            .ToListAsync(ct);

        var expenseIds = expenses.Select(e => e.Id).ToList();

        var splits = await _db.ExpenseSplits
            .Where(s => expenseIds.Contains(s.ExpenseId) && relevantUserIds.Contains(s.UserId))
            .Select(s => new { s.ExpenseId, s.UserId, s.AmountOwedCents })
            .ToListAsync(ct);

        var settlements = await _db.Settlements
            .Where(s => allSharedGroupIds.Contains(s.GroupId)
                && (s.FromUserId == ownerUserId || s.ToUserId == ownerUserId)
                && (friendUserIds.Contains(s.FromUserId) || friendUserIds.Contains(s.ToUserId)))
            .Select(s => new { s.GroupId, s.FromUserId, s.ToUserId, s.AmountCents })
            .ToListAsync(ct);

        var expensesByGroup = expenses
            .GroupBy(e => e.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var splitsByExpense = splits
            .GroupBy(s => s.ExpenseId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var settlementsByGroup = settlements
            .GroupBy(s => s.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<Guid, (long NetBalanceCents, int SharedGroupCount, bool HasActiveBalances)>();

        foreach (var friendId in friendUserIds)
        {
            if (!sharedGroupsByFriend.TryGetValue(friendId, out var friendGroupIds))
            {
                result[friendId] = (0L, 0, false);
                continue;
            }

            long netBalance = 0;
            var hasActive = false;

            foreach (var groupId in friendGroupIds)
            {
                long groupBalance = 0;

                var groupExpenses = expensesByGroup.GetValueOrDefault(groupId, []);
                foreach (var expense in groupExpenses)
                {
                    var expenseSplits = splitsByExpense.GetValueOrDefault(expense.Id, []);
                    if (expense.PayerUserId == ownerUserId)
                    {
                        var friendSplit = expenseSplits.FirstOrDefault(s => s.UserId == friendId);
                        if (friendSplit is not null) groupBalance += friendSplit.AmountOwedCents;
                    }
                    else if (expense.PayerUserId == friendId)
                    {
                        var ownerSplit = expenseSplits.FirstOrDefault(s => s.UserId == ownerUserId);
                        if (ownerSplit is not null) groupBalance -= ownerSplit.AmountOwedCents;
                    }
                }

                var groupSettlements = settlementsByGroup.GetValueOrDefault(groupId, []);
                foreach (var settlement in groupSettlements)
                {
                    if (settlement.FromUserId == ownerUserId && settlement.ToUserId == friendId)
                        groupBalance += settlement.AmountCents;
                    else if (settlement.FromUserId == friendId && settlement.ToUserId == ownerUserId)
                        groupBalance -= settlement.AmountCents;
                }

                netBalance += groupBalance;
                if (groupBalance != 0) hasActive = true;
            }

            result[friendId] = (netBalance, friendGroupIds.Count, hasActive);
        }

        return result;
    }

    /// <summary>
    /// For two connected users, compute the per-group balance breakdown.
    /// Balance is from userA's perspective: positive = userB owes userA.
    /// </summary>
    private async Task<(List<SharedGroupBalance> Groups, long NetBalanceCents)>
        ComputePerGroupBalancesAsync(Guid userA, Guid userB, CancellationToken ct)
    {
        // Find all groups both users share
        var userAGroups = await _db.GroupMembers
            .Where(m => m.UserId == userA)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        var sharedGroupIds = await _db.GroupMembers
            .Where(m => m.UserId == userB && userAGroups.Contains(m.GroupId))
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        if (sharedGroupIds.Count == 0)
        {
            return (new List<SharedGroupBalance>(), 0L);
        }

        var groups = await _db.Groups
            .Where(g => sharedGroupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        // Load all expenses in shared groups
        var expenses = await _db.Expenses
            .Where(e => sharedGroupIds.Contains(e.GroupId))
            .ToListAsync(ct);

        var expenseIds = expenses.Select(e => e.Id).ToList();
        var splits = expenseIds.Count == 0
            ? new List<ExpenseSplitEntity>()
            : await _db.ExpenseSplits
                .Where(s => expenseIds.Contains(s.ExpenseId))
                .ToListAsync(ct);

        var splitsByExpense = splits
            .GroupBy(s => s.ExpenseId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Load settlements in shared groups between these two users
        var settlements = await _db.Settlements
            .Where(s => sharedGroupIds.Contains(s.GroupId)
                && ((s.FromUserId == userA && s.ToUserId == userB)
                    || (s.FromUserId == userB && s.ToUserId == userA)))
            .ToListAsync(ct);

        var expensesByGroup = expenses
            .GroupBy(e => e.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var settlementsByGroup = settlements
            .GroupBy(s => s.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<SharedGroupBalance>();
        long totalNet = 0;

        foreach (var groupId in sharedGroupIds)
        {
            long groupBalance = 0;

            // Expenses: for each expense in this group, compute the net between userA and userB.
            var groupExpenses = expensesByGroup.GetValueOrDefault(groupId, []);
            foreach (var expense in groupExpenses)
            {
                var expenseSplits = splitsByExpense.GetValueOrDefault(expense.Id, new List<ExpenseSplitEntity>());

                // What userA paid → credits userA
                if (expense.PayerUserId == userA)
                {
                    // userB's split in this expense = what userB owes userA for this expense
                    var userBSplit = expenseSplits.FirstOrDefault(s => s.UserId == userB);
                    if (userBSplit is not null)
                    {
                        groupBalance += userBSplit.AmountOwedCents;
                    }
                }
                // What userB paid → debits userA
                else if (expense.PayerUserId == userB)
                {
                    var userASplit = expenseSplits.FirstOrDefault(s => s.UserId == userA);
                    if (userASplit is not null)
                    {
                        groupBalance -= userASplit.AmountOwedCents;
                    }
                }
            }

            // Settlements between users in this group
            var groupSettlements = settlementsByGroup.GetValueOrDefault(groupId, []);
            foreach (var settlement in groupSettlements)
            {
                if (settlement.FromUserId == userA && settlement.ToUserId == userB)
                {
                    // userA paid userB → A is settling debt to B → from A's perspective, balance increases
                    groupBalance += settlement.AmountCents;
                }
                else if (settlement.FromUserId == userB && settlement.ToUserId == userA)
                {
                    // userB paid userA → B is settling debt to A → from A's perspective, balance decreases
                    groupBalance -= settlement.AmountCents;
                }
            }

            if (!groups.TryGetValue(groupId, out var group)) continue;

            var balanceLabel = FormatGroupBalanceLabel(groupBalance);
            var hasUnsettled = groupExpenses.Any(); // simplified: if there are expenses, there may be unsettled

            result.Add(new SharedGroupBalance(
                groupId,
                group.Name,
                groupBalance,
                balanceLabel,
                groupBalance != 0));

            totalNet += groupBalance;
        }

        return (result, totalNet);
    }

    private static string FormatBalanceLabel(long cents, string friendDisplayName)
    {
        if (cents == 0) return "All settled";
        var amount = FormatCurrency(Math.Abs(cents));
        return cents > 0
            ? $"{friendDisplayName} owes you {amount}"
            : $"You owe {friendDisplayName} {amount}";
    }

    private static string FormatGroupBalanceLabel(long cents)
    {
        if (cents == 0) return "Settled";
        var amount = FormatCurrency(Math.Abs(cents));
        return cents > 0
            ? $"They owe you {amount}"
            : $"You owe {amount}";
    }

    private static string FormatCurrency(long absCents)
    {
        var dollars = absCents / 100m;
        return $"${dollars:F2}";
    }

    // Records for API responses
    public record FriendListItem(
        Guid Id,
        string DisplayName,
        string Email,
        string Status,
        string? FriendUserId,
        long NetBalanceCents,
        string NetBalanceLabel,
        int SharedGroupCount,
        bool HasActiveSharedBalances,
        string LastUsedAtUtc);

    public record FriendDetail(
        Guid Id,
        string DisplayName,
        string Email,
        string Status,
        string? FriendUserId,
        long NetBalanceCents,
        string NetBalanceLabel,
        string Summary,
        List<SharedGroupBalance> SharedGroups);

    public record SharedGroupBalance(
        Guid GroupId,
        string GroupName,
        long BalanceCents,
        string BalanceLabel,
        bool HasUnsettledExpenses);
}
