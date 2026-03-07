using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Services;

public class GroupService
{
    private readonly AppDbContext _db;

    public GroupService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<GroupEntity> CreateGroupAsync(Guid creatorUserId, string name, CancellationToken ct = default)
    {
        var group = new GroupEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedBy = creatorUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Groups.Add(group);

        _db.GroupMembers.Add(new GroupMemberEntity
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = creatorUserId,
            Role = Role.OWNER,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<List<GroupEntity>> ListGroupsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var groupIds = await _db.GroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        return await _db.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(ct);
    }

    public async Task<GroupDetails> GetGroupDetailsAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var requesterMembership = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);
        if (requesterMembership is null)
        {
            throw new AuthorizationException("access", "group " + groupId);
        }

        var group = await _db.Groups.FindAsync(new object[] { groupId }, ct);
        if (group is null)
        {
            throw new ResourceNotFoundException("Group", groupId.ToString());
        }

        var groupMembers = await _db.GroupMembers
            .Where(m => m.GroupId == groupId)
            .ToListAsync(ct);
        var memberUserIds = groupMembers.Select(m => m.UserId).Distinct().ToHashSet();

        var usersById = await _db.Users
            .Where(u => memberUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var allGroupExpenses = await _db.Expenses
            .Where(e => e.GroupId == groupId)
            .ToListAsync(ct);
        var recentGroupExpenses = allGroupExpenses
            .OrderByDescending(e => e.CreatedAt)
            .Take(50)
            .ToList();
        var allExpenseIds = allGroupExpenses.Select(e => e.Id).ToList();

        var splitRows = allExpenseIds.Count == 0
            ? new List<ExpenseSplitEntity>()
            : await _db.ExpenseSplits.Where(s => allExpenseIds.Contains(s.ExpenseId)).ToListAsync(ct);
        var splitsByExpense = splitRows.GroupBy(s => s.ExpenseId).ToDictionary(g => g.Key, g => g.ToList());

        var netByUserId = memberUserIds.ToDictionary(id => id, _ => 0L);
        foreach (var expense in allGroupExpenses)
        {
            netByUserId[expense.PayerUserId] = CheckedAdd(
                netByUserId[expense.PayerUserId],
                expense.AmountCents,
                "net balance for payer");
            if (splitsByExpense.TryGetValue(expense.Id, out var expenseSplits))
            {
                foreach (var split in expenseSplits)
                {
                    netByUserId[split.UserId] = CheckedSubtract(
                        netByUserId[split.UserId],
                        split.AmountOwedCents,
                        "net balance for split");
                }
            }
        }

        var memberInfos = groupMembers
            .OrderBy(m => m.JoinedAt)
            .Select(m =>
            {
                if (!usersById.TryGetValue(m.UserId, out var user))
                {
                    throw new ResourceNotFoundException("User", m.UserId.ToString());
                }

                return new MemberInfo(
                    m.UserId,
                    user.DisplayName,
                    user.Email,
                    m.Role,
                    m.JoinedAt,
                    netByUserId.GetValueOrDefault(m.UserId, 0L));
            })
            .ToList();

        var expenseInfos = recentGroupExpenses.Select(expense =>
        {
            var mappedSplits = splitsByExpense.GetValueOrDefault(expense.Id, new List<ExpenseSplitEntity>())
                .OrderBy(s => s.UserId.ToString())
                .Select(s => new ExpenseSplitInfo(s.UserId, s.AmountOwedCents))
                .ToList();

            return new ExpenseInfo(
                expense.Id,
                expense.Description,
                expense.AmountCents,
                expense.PayerUserId,
                expense.CreatedByUserId,
                expense.ReversalOfExpenseId,
                expense.CreatedAt,
                mappedSplits);
        }).ToList();

        var totalExpenseAmountCents = CheckedSum(allGroupExpenses.Select(e => e.AmountCents), "total expense amount");
        return new GroupDetails(
            new GroupInfo(group.Id, group.Name, group.CreatedBy, group.CreatedAt),
            new MeInfo(userId, requesterMembership.Role, netByUserId.GetValueOrDefault(userId, 0L)),
            memberInfos,
            expenseInfos,
            new Summary(allGroupExpenses.Count, totalExpenseAmountCents));
    }

    public async Task DeleteGroupAsync(Guid groupId, Guid actorUserId, CancellationToken ct = default)
    {
        var group = await _db.Groups.FindAsync(new object[] { groupId }, ct);
        if (group is null)
        {
            throw new ResourceNotFoundException("Group", groupId.ToString());
        }

        if (group.CreatedBy != actorUserId)
        {
            throw new AuthorizationException("delete", "group " + groupId);
        }

        _db.Groups.Remove(group);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RequireMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var membership = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);
        if (membership is null)
        {
            throw new AuthorizationException("access", "group " + groupId);
        }
    }

    public async Task RequireMembersAsync(Guid groupId, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var distinct = userIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        var present = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && distinct.Contains(m.UserId))
            .Select(m => m.UserId)
            .Distinct()
            .CountAsync(ct);

        if (present != distinct.Count)
        {
            throw new ValidationException("One or more users are not members of group " + groupId);
        }
    }

    public async Task<List<PendingInvite>> ListPendingInvitesForEmailAsync(string email, Guid requesterUserId, CancellationToken ct = default)
    {
        var normalizedQueryEmail = NormalizeInviteEmail(email);
        var requester = await _db.Users.FindAsync(new object[] { requesterUserId }, ct);
        if (requester is null)
        {
            throw new ResourceNotFoundException("User", requesterUserId.ToString());
        }

        var normalizedRequesterEmail = NormalizeInviteEmail(requester.Email);
        if (!string.Equals(normalizedQueryEmail, normalizedRequesterEmail, StringComparison.Ordinal))
        {
            throw new AuthorizationException("list invites for another user");
        }

        var now = DateTimeOffset.UtcNow;
        var pending = await (
            from i in _db.Invites
            join g in _db.Groups on i.GroupId equals g.Id
            where i.Email.ToLower() == normalizedQueryEmail && i.ExpiresAt > now
            orderby i.CreatedAt descending
            select new PendingInvite(
                i.Id,
                i.GroupId,
                g.Name,
                i.Email.ToLower(),
                i.ExpiresAt,
                i.CreatedAt))
            .ToListAsync(ct);

        return pending;
    }

    public async Task<CreatedInvite> CreateInviteAsync(Guid groupId, string email, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeInviteEmail(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (user is not null)
        {
            var alreadyMember = await _db.GroupMembers
                .AnyAsync(m => m.GroupId == groupId && m.UserId == user.Id, ct);
            if (alreadyMember)
            {
                throw new ConflictException("User is already a member of this group");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var existingInvite = await _db.Invites
            .FirstOrDefaultAsync(i => i.GroupId == groupId && i.Email.ToLower() == normalizedEmail, ct);

        if (existingInvite is not null)
        {
            if (existingInvite.ExpiresAt > now)
            {
                throw new ConflictException("Invite already pending for this email in this group");
            }

            _db.Invites.Remove(existingInvite);
        }

        var token = TokenCodec.RandomUrlToken(32);
        var tokenHash = TokenCodec.Sha256Base64Url(token);
        var expiresAt = now.AddDays(7);

        _db.Invites.Add(new InviteEntity
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            Email = normalizedEmail,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);
        return new CreatedInvite(token, normalizedEmail, expiresAt);
    }

    public async Task CancelInviteAsync(Guid groupId, string rawToken, Guid actorUserId, CancellationToken ct = default)
    {
        await RequireMemberAsync(groupId, actorUserId, ct);
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new ValidationException("Invite token is required");
        }

        var tokenHash = TokenCodec.Sha256Base64Url(rawToken);
        var deleted = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"delete from invites where group_id = {groupId} and token_hash = {tokenHash}",
            ct);
        if (deleted == 0)
        {
            throw new ResourceNotFoundException("Invite", "token hash");
        }
    }

    public async Task AcceptInviteAsync(string rawToken, Guid userId, CancellationToken ct = default)
    {
        var tokenHash = TokenCodec.Sha256Base64Url(rawToken);
        var invite = await _db.Invites.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
        if (invite is null)
        {
            throw new ResourceNotFoundException("Invite", "token");
        }

        if (invite.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new ValidationException("Invite has expired");
        }

        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        if (!string.Equals(invite.Email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Invite email does not match authenticated user");
        }

        var deleted = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"delete from invites where token_hash = {tokenHash}", ct);
        if (deleted == 0)
        {
            throw new ConflictException("Invite already used");
        }

        var membership = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == invite.GroupId && m.UserId == userId, ct);
        if (membership is null)
        {
            _db.GroupMembers.Add(new GroupMemberEntity
            {
                Id = Guid.NewGuid(),
                GroupId = invite.GroupId,
                UserId = userId,
                Role = Role.MEMBER,
                JoinedAt = DateTimeOffset.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task AcceptInviteByIdAsync(Guid inviteId, Guid userId, CancellationToken ct = default)
    {
        var invite = await _db.Invites.FindAsync(new object[] { inviteId }, ct);
        if (invite is null)
        {
            throw new ResourceNotFoundException("Invite", inviteId.ToString());
        }

        if (invite.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new ValidationException("Invite has expired");
        }

        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        if (!string.Equals(invite.Email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Invite email does not match authenticated user");
        }

        var deleted = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"delete from invites where id = {invite.Id} and token_hash = {invite.TokenHash}",
            ct);
        if (deleted == 0)
        {
            throw new ConflictException("Invite already used");
        }

        var membership = await _db.GroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == invite.GroupId && m.UserId == userId, ct);
        if (membership is null)
        {
            _db.GroupMembers.Add(new GroupMemberEntity
            {
                Id = Guid.NewGuid(),
                GroupId = invite.GroupId,
                UserId = userId,
                Role = Role.MEMBER,
                JoinedAt = DateTimeOffset.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string NormalizeInviteEmail(string email)
    {
        var normalized = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ValidationException("Email is required");
        }

        return normalized;
    }

    private static long CheckedAdd(long left, long right, string context)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw new ValidationException($"Amount overflow while calculating {context}");
        }
    }

    private static long CheckedSubtract(long left, long right, string context)
    {
        try
        {
            return checked(left - right);
        }
        catch (OverflowException)
        {
            throw new ValidationException($"Amount overflow while calculating {context}");
        }
    }

    private static long CheckedSum(IEnumerable<long> amounts, string context)
    {
        long total = 0;
        foreach (var amount in amounts)
        {
            total = CheckedAdd(total, amount, context);
        }

        return total;
    }

    public record CreatedInvite(string Token, string Email, DateTimeOffset ExpiresAt);
    public record PendingInvite(Guid Id, Guid GroupId, string GroupName, string Email, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
    public record GroupDetails(
        GroupInfo Group,
        MeInfo Me,
        List<MemberInfo> Members,
        List<ExpenseInfo> Expenses,
        Summary Summary);
    public record GroupInfo(Guid Id, string Name, Guid CreatedBy, DateTimeOffset CreatedAt);
    public record MeInfo(Guid UserId, Role Role, long NetBalanceCents);
    public record MemberInfo(Guid UserId, string DisplayName, string Email, Role Role, DateTimeOffset JoinedAt, long NetBalanceCents);
    public record ExpenseInfo(
        Guid Id,
        string Description,
        long AmountCents,
        Guid PayerUserId,
        Guid CreatedByUserId,
        Guid? ReversalOfExpenseId,
        DateTimeOffset CreatedAt,
        List<ExpenseSplitInfo> Splits);
    public record ExpenseSplitInfo(Guid UserId, long AmountOwedCents);
    public record Summary(int ExpenseCount, long TotalExpenseAmountCents);
}
