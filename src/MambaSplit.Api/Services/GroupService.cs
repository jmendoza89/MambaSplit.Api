using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Security;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Services;

public class GroupService
{
    private readonly AppDbContext _db;
    private readonly TransactionalEmailService _transactionalEmailService;
    private readonly ILogger<GroupService> _logger;

    private readonly GroupMembershipService _groupMembershipService;

    public GroupService(AppDbContext db, TransactionalEmailService transactionalEmailService, ILogger<GroupService> logger, GroupMembershipService groupMembershipService)
    {
        _db = db;
        _transactionalEmailService = transactionalEmailService;
        _logger = logger;
        _groupMembershipService = groupMembershipService;
    }

    public async Task<GroupEntity> CreateGroupAsync(Guid creatorUserId, string name, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var group = new GroupEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedBy = creatorUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Groups.Add(group);
        await _db.SaveChangesAsync(ct);

        _db.GroupMembers.Add(new GroupMemberEntity
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = creatorUserId,
            Role = Role.OWNER,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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
        var settlementExpenseLinks = allExpenseIds.Count == 0
            ? new List<SettlementExpenseEntity>()
            : await _db.SettlementExpenses
                .Where(se => allExpenseIds.Contains(se.ExpenseId))
                .ToListAsync(ct);
        var settlementIdByExpenseId = settlementExpenseLinks
            .GroupBy(se => se.ExpenseId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.SettlementId).Select(x => x.SettlementId).First());
        var expenseIdsBySettlementId = settlementExpenseLinks
            .GroupBy(se => se.SettlementId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ExpenseId).ToList());

        var allGroupSettlements = await _db.Settlements
            .Where(s => s.GroupId == groupId)
            .ToListAsync(ct);
        var recentGroupSettlements = allGroupSettlements
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToList();

        var splitRows = allExpenseIds.Count == 0
            ? new List<ExpenseSplitEntity>()
            : await _db.ExpenseSplits.Where(s => allExpenseIds.Contains(s.ExpenseId)).ToListAsync(ct);
        var splitsByExpense = splitRows.GroupBy(s => s.ExpenseId).ToDictionary(g => g.Key, g => g.ToList());

        var netByUserId = memberUserIds.ToDictionary(id => id, _ => 0L);

        // Ensure all users referenced in historical data have an entry so that
        // departed members (whose splits in settled or reversed expenses were not
        // rebalanced) do not cause KeyNotFoundException.
        foreach (var expense in allGroupExpenses)
            netByUserId.TryAdd(expense.PayerUserId, 0L);
        foreach (var split in splitRows)
            netByUserId.TryAdd(split.UserId, 0L);
        foreach (var settlement in allGroupSettlements)
        {
            netByUserId.TryAdd(settlement.FromUserId, 0L);
            netByUserId.TryAdd(settlement.ToUserId, 0L);
        }

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

        foreach (var settlement in allGroupSettlements)
        {
            netByUserId[settlement.FromUserId] = CheckedAdd(
                netByUserId[settlement.FromUserId],
                settlement.AmountCents,
                "net balance for settlement payer");
            netByUserId[settlement.ToUserId] = CheckedSubtract(
                netByUserId[settlement.ToUserId],
                settlement.AmountCents,
                "net balance for settlement receiver");
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
            var settlementId = settlementIdByExpenseId.TryGetValue(expense.Id, out var sid) ? (Guid?)sid : null;

            return new ExpenseInfo(
                expense.Id,
                expense.Description,
                expense.AmountCents,
                expense.PayerUserId,
                expense.CreatedByUserId,
                expense.ReversalOfExpenseId,
                expense.CreatedAt,
                settlementId,
                mappedSplits);
        }).ToList();

        var settlementInfos = recentGroupSettlements.Select(settlement =>
        {
            if (!usersById.TryGetValue(settlement.FromUserId, out var fromUser))
            {
                throw new ResourceNotFoundException("User", settlement.FromUserId.ToString());
            }

            if (!usersById.TryGetValue(settlement.ToUserId, out var toUser))
            {
                throw new ResourceNotFoundException("User", settlement.ToUserId.ToString());
            }

            return new SettlementInfo(
                settlement.Id,
                settlement.GroupId,
                settlement.FromUserId,
                fromUser.DisplayName,
                settlement.ToUserId,
                toUser.DisplayName,
                settlement.AmountCents,
                settlement.Note,
                settlement.CreatedAt,
                expenseIdsBySettlementId.GetValueOrDefault(settlement.Id, []));
        }).ToList();

        var settlementSuggestions = BuildSettlementSuggestions(memberInfos);
        var totalExpenseAmountCents = CheckedSum(allGroupExpenses.Select(e => e.AmountCents), "total expense amount");
        var totalSettlementAmountCents = CheckedSum(
            allGroupSettlements.Select(s => s.AmountCents),
            "total settlement amount");
        return new GroupDetails(
            new GroupInfo(group.Id, group.Name, group.CreatedBy, group.CreatedAt),
            new MeInfo(userId, requesterMembership.Role, netByUserId.GetValueOrDefault(userId, 0L)),
            memberInfos,
            expenseInfos,
            settlementInfos,
            settlementSuggestions,
            new Summary(allGroupExpenses.Count, totalExpenseAmountCents, allGroupSettlements.Count, totalSettlementAmountCents));
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

    public async Task LeaveGroupAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _groupMembershipService.RemoveMemberAndRebalanceAsync(groupId, userId, ct);
        await tx.CommitAsync(ct);
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
        var invites = await _db.Invites
            .Where(invite => invite.SentToEmail == normalizedQueryEmail)
            .ToListAsync(ct);
        var pendingInvites = invites
            .Where(invite => invite.ExpiresAt > now)
            .OrderByDescending(invite => invite.CreatedAt)
            .ToList();

        var groupIds = pendingInvites.Select(invite => invite.GroupId).Distinct().ToList();
        var senderIds = pendingInvites.Select(invite => invite.SentByUserId).Distinct().ToList();
        var groupsById = groupIds.Count == 0
            ? new Dictionary<Guid, GroupEntity>()
            : await _db.Groups.Where(group => groupIds.Contains(group.Id)).ToDictionaryAsync(group => group.Id, ct);
        var sendersById = senderIds.Count == 0
            ? new Dictionary<Guid, UserEntity>()
            : await _db.Users.Where(user => senderIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, ct);

        return pendingInvites
            .Select(invite =>
            {
                if (!groupsById.TryGetValue(invite.GroupId, out var group))
                {
                    throw new ResourceNotFoundException("Group", invite.GroupId.ToString());
                }

                if (!sendersById.TryGetValue(invite.SentByUserId, out var sender))
                {
                    throw new ResourceNotFoundException("User", invite.SentByUserId.ToString());
                }

                return new PendingInvite(
                invite.Id,
                invite.GroupId,
                group.Name,
                invite.SentByUserId,
                sender.Email,
                sender.DisplayName,
                invite.SentToEmail,
                invite.ExpiresAt,
                invite.CreatedAt);
            })
            .ToList();
    }

    public async Task<List<SentInvite>> ListSentInvitesForUserAsync(Guid senderUserId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sender = await _db.Users.FindAsync(new object[] { senderUserId }, ct);
        if (sender is null)
        {
            throw new ResourceNotFoundException("User", senderUserId.ToString());
        }

        var invites = await _db.Invites
            .Where(invite => invite.SentByUserId == senderUserId)
            .ToListAsync(ct);
        var sentInvites = invites
            .Where(invite => invite.ExpiresAt > now)
            .OrderByDescending(invite => invite.CreatedAt)
            .ToList();

        var groupIds = sentInvites.Select(invite => invite.GroupId).Distinct().ToList();
        var groupsById = groupIds.Count == 0
            ? new Dictionary<Guid, GroupEntity>()
            : await _db.Groups.Where(group => groupIds.Contains(group.Id)).ToDictionaryAsync(group => group.Id, ct);

        return sentInvites
            .Select(invite =>
            {
                if (!groupsById.TryGetValue(invite.GroupId, out var group))
                {
                    throw new ResourceNotFoundException("Group", invite.GroupId.ToString());
                }

                return new SentInvite(
                invite.Id,
                invite.GroupId,
                group.Name,
                invite.SentByUserId,
                sender.Email,
                sender.DisplayName,
                invite.SentToEmail,
                invite.ExpiresAt,
                invite.CreatedAt);
            })
            .ToList();
    }

    public async Task<CreatedInvite> CreateInviteAsync(
        Guid groupId,
        string email,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeInviteEmail(email);
        var actor = await _db.Users.FindAsync(new object[] { actorUserId }, ct);
        if (actor is null)
        {
            throw new ResourceNotFoundException("User", actorUserId.ToString());
        }

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
            .FirstOrDefaultAsync(i => i.GroupId == groupId && i.SentToEmail.ToLower() == normalizedEmail, ct);

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
            SentByUserId = actorUserId,
            SentToEmail = normalizedEmail,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);
        await SendInviteEmailAsync(groupId, actorUserId, normalizedEmail, token, expiresAt, ct);
        return new CreatedInvite(token, actorUserId, actor.Email, actor.DisplayName, normalizedEmail, expiresAt);
    }
    private async Task SendInviteEmailAsync(Guid groupId, Guid actorUserId, string recipientEmail, string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        try
        {
            var group = await _db.Groups.FindAsync(new object[] { groupId }, ct);
            var actor = await _db.Users.FindAsync(new object[] { actorUserId }, ct);
            if (group is null || actor is null)
            {
                _logger.LogWarning("Skipping invite email send because group or actor was not found. groupId={GroupId} actorUserId={ActorUserId}", groupId, actorUserId);
                return;
            }

            var model = new JsonObject
            {
                ["groupName"] = group.Name,
                ["inviterName"] = actor.DisplayName,
                ["inviteToken"] = token,
                ["inviteExpiresInText"] = FormatInviteExpiresInText(expiresAt),
                ["inviteExpiresAtTooltip"] = expiresAt.UtcDateTime.ToString("MMMM d, yyyy 'at' h:mm tt 'UTC'"),
            };

            await _transactionalEmailService.SendTemplateAsync(
                "invite",
                [recipientEmail],
                [],
                [],
                null,
                model,
                ["invite", "group:" + groupId.ToString("N")],
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invite email send failed for groupId={GroupId} recipient={RecipientEmail}", groupId, recipientEmail);
        }
    }

    private async Task SendInviteDeclinedEmailAsync(
        Guid groupId,
        Guid senderUserId,
        string inviteeName,
        string inviteeEmail,
        DateTimeOffset declinedAt,
        CancellationToken ct)
    {
        try
        {
            var group = await _db.Groups.FindAsync(new object[] { groupId }, ct);
            var sender = await _db.Users.FindAsync(new object[] { senderUserId }, ct);
            if (group is null || sender is null || string.IsNullOrWhiteSpace(sender.Email))
            {
                _logger.LogWarning(
                    "Skipping invite-declined email send because group or sender was not found. groupId={GroupId} senderUserId={SenderUserId}",
                    groupId,
                    senderUserId);
                return;
            }

            var displayName = string.IsNullOrWhiteSpace(inviteeName) ? inviteeEmail : inviteeName;
            var model = new JsonObject
            {
                ["groupName"] = group.Name,
                ["groupId"] = groupId.ToString(),
                ["inviteeName"] = displayName,
                ["inviteeEmail"] = inviteeEmail,
                ["declinedAtDisplay"] = declinedAt.UtcDateTime.ToString("MMMM d, yyyy 'at' h:mm tt 'UTC'"),
            };

            await _transactionalEmailService.SendTemplateAsync(
                "invite-declined",
                new List<string> { sender.Email },
                new List<string>(),
                new List<string>(),
                null,
                model,
                new List<string> { "invite-declined", "group:" + groupId.ToString("N") },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invite-declined email send failed for groupId={GroupId} senderUserId={SenderUserId}", groupId, senderUserId);
        }
    }

    public async Task<List<GroupInvite>> ListGroupInvitesAsync(Guid groupId, Guid actorUserId, CancellationToken ct = default)
    {
        await RequireMemberAsync(groupId, actorUserId, ct);
        var actor = await _db.Users.FindAsync(new object[] { actorUserId }, ct);
        if (actor is null)
        {
            throw new ResourceNotFoundException("User", actorUserId.ToString());
        }

        var now = DateTimeOffset.UtcNow;
        var invites = await _db.Invites
            .Where(invite => invite.GroupId == groupId && invite.SentByUserId == actorUserId)
            .ToListAsync(ct);

        return invites
            .Where(invite => invite.ExpiresAt > now)
            .OrderByDescending(invite => invite.CreatedAt)
            .Select(invite => new GroupInvite(
                invite.Id,
                invite.GroupId,
                invite.SentByUserId,
                actor.Email,
                actor.DisplayName,
                invite.SentToEmail,
                invite.ExpiresAt,
                invite.CreatedAt))
            .ToList();
    }

    public async Task CancelInviteAsync(Guid groupId, string rawToken, Guid actorUserId, CancellationToken ct = default)
    {
        await RequireMemberAsync(groupId, actorUserId, ct);
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new ValidationException("Invite token is required");
        }

        var tokenHash = TokenCodec.Sha256Base64Url(rawToken);
        var invite = await _db.Invites
            .FirstOrDefaultAsync(i => i.GroupId == groupId && i.TokenHash == tokenHash, ct);
        if (invite is null)
        {
            throw new ResourceNotFoundException("Invite", "token hash");
        }

        if (invite.SentByUserId != actorUserId)
        {
            throw new AuthorizationException("cancel", $"invite {invite.Id}");
        }

        _db.Invites.Remove(invite);
        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelInviteByIdAsync(Guid groupId, Guid inviteId, Guid actorUserId, CancellationToken ct = default)
    {
        await RequireMemberAsync(groupId, actorUserId, ct);

        var invite = await _db.Invites.FindAsync(new object[] { inviteId }, ct);
        if (invite is null)
        {
            throw new ResourceNotFoundException("Invite", inviteId.ToString());
        }

        if (invite.GroupId != groupId)
        {
            throw new ResourceNotFoundException("Invite", inviteId.ToString());
        }

        if (invite.SentByUserId != actorUserId)
        {
            throw new AuthorizationException("cancel", $"invite {invite.Id}");
        }

        _db.Invites.Remove(invite);
        await _db.SaveChangesAsync(ct);
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

        if (!string.Equals(invite.SentToEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Invite email does not match authenticated user");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var deleted = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"delete from invites where token_hash = {tokenHash}", ct);
        if (deleted == 0)
        {
            throw new ConflictException("Invite already used");
        }

        await _groupMembershipService.AddMemberAndRebalanceAsync(invite.GroupId, userId, Role.MEMBER, ct);

        await tx.CommitAsync(ct);
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

        if (!string.Equals(invite.SentToEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Invite email does not match authenticated user");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var deleted = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"delete from invites where id = {invite.Id} and token_hash = {invite.TokenHash}",
            ct);
        if (deleted == 0)
        {
            throw new ConflictException("Invite already used");
        }

        await _groupMembershipService.AddMemberAndRebalanceAsync(invite.GroupId, userId, Role.MEMBER, ct);

        await tx.CommitAsync(ct);
    }

    public async Task DeclineInviteAsync(string rawToken, Guid userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new ValidationException("Invite token is required");
        }

        var tokenHash = TokenCodec.Sha256Base64Url(rawToken);
        var invite = await _db.Invites.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
        if (invite is null)
        {
            throw new ResourceNotFoundException("Invite", "token");
        }

        await DeclineInviteCoreAsync(invite, userId, deleteById: false, ct);
    }

    public async Task DeclineInviteByIdAsync(Guid inviteId, Guid userId, CancellationToken ct = default)
    {
        _logger.LogInformation("DeclineInviteByIdAsync: Starting decline for inviteId {InviteId}", inviteId);
        var invite = await _db.Invites.FindAsync(new object[] { inviteId }, ct);
        if (invite is null)
        {
            throw new ResourceNotFoundException("Invite", inviteId.ToString());
        }

        _logger.LogInformation("DeclineInviteByIdAsync: Found invite, calling DeclineInviteCoreAsync");
        await DeclineInviteCoreAsync(invite, userId, deleteById: true, ct);
    }

    private async Task DeclineInviteCoreAsync(InviteEntity invite, Guid userId, bool deleteById, CancellationToken ct)
    {
        _logger.LogInformation("DeclineInviteCoreAsync: Starting decline for invite {InviteId}", invite.Id);
        
        if (invite.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new ValidationException("Invite has expired");
        }

        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        if (!string.Equals(invite.SentToEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Invite email does not match authenticated user");
        }

        var declinedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("DeclineInviteCoreAsync: About to delete invite {InviteId}", invite.Id);
        var deleted = deleteById
            ? await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"delete from invites where id = {invite.Id} and token_hash = {invite.TokenHash}",
                ct)
            : await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"delete from invites where token_hash = {invite.TokenHash}",
                ct);
        _logger.LogInformation("DeclineInviteCoreAsync: Delete result: {DeletedRows}", deleted);
        if (deleted == 0)
        {
            throw new ConflictException("Invite already resolved");
        }

        _logger.LogInformation("DeclineInviteCoreAsync: Invite deleted successfully");
        await SendInviteDeclinedEmailAsync(invite.GroupId, invite.SentByUserId, user.DisplayName, user.Email, declinedAt, ct);
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

    private static string FormatInviteExpiresInText(DateTimeOffset expiresAt)
    {
        var remainingDays = Math.Max(1, (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalDays));
        return remainingDays == 1 ? "1 day left" : $"{remainingDays} days left";
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

    private static List<SettlementSuggestion> BuildSettlementSuggestions(List<MemberInfo> memberInfos)
    {
        var creditors = memberInfos
            .Where(m => m.NetBalanceCents > 0)
            .Select(m => new BalanceNode(m.UserId, m.DisplayName, m.NetBalanceCents))
            .OrderBy(m => m.UserId.ToString())
            .ToList();
        var debtors = memberInfos
            .Where(m => m.NetBalanceCents < 0)
            .Select(m => new BalanceNode(m.UserId, m.DisplayName, checked(-m.NetBalanceCents)))
            .OrderBy(m => m.UserId.ToString())
            .ToList();

        var suggestions = new List<SettlementSuggestion>();
        var creditorIdx = 0;
        var debtorIdx = 0;

        while (creditorIdx < creditors.Count && debtorIdx < debtors.Count)
        {
            var creditor = creditors[creditorIdx];
            var debtor = debtors[debtorIdx];
            var amount = Math.Min(creditor.RemainingCents, debtor.RemainingCents);
            if (amount <= 0)
            {
                break;
            }

            suggestions.Add(new SettlementSuggestion(
                debtor.UserId,
                debtor.DisplayName,
                creditor.UserId,
                creditor.DisplayName,
                amount));

            creditor.RemainingCents -= amount;
            debtor.RemainingCents -= amount;

            if (creditor.RemainingCents == 0)
            {
                creditorIdx++;
            }

            if (debtor.RemainingCents == 0)
            {
                debtorIdx++;
            }
        }

        return suggestions;
    }

    public record CreatedInvite(
        string Token,
        Guid SentByUserId,
        string SentByEmail,
        string SentByDisplayName,
        string SentToEmail,
        DateTimeOffset ExpiresAt);
    public record GroupInvite(
        Guid Id,
        Guid GroupId,
        Guid SentByUserId,
        string SentByEmail,
        string SentByDisplayName,
        string SentToEmail,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt);
    public record PendingInvite(
        Guid Id,
        Guid GroupId,
        string GroupName,
        Guid SentByUserId,
        string SentByEmail,
        string SentByDisplayName,
        string SentToEmail,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt);
    public record SentInvite(
        Guid Id,
        Guid GroupId,
        string GroupName,
        Guid SentByUserId,
        string SentByEmail,
        string SentByDisplayName,
        string SentToEmail,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt);
    public record GroupDetails(
        GroupInfo Group,
        MeInfo Me,
        List<MemberInfo> Members,
        List<ExpenseInfo> Expenses,
        List<SettlementInfo> Settlements,
        List<SettlementSuggestion> SettlementSuggestions,
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
        Guid? SettlementId,
        List<ExpenseSplitInfo> Splits);
    public record SettlementInfo(
        Guid Id,
        Guid GroupId,
        Guid FromUserId,
        string FromUserName,
        Guid ToUserId,
        string ToUserName,
        long AmountCents,
        string? Note,
        DateTimeOffset CreatedAt,
        List<Guid> ExpenseIds);
    public record SettlementSuggestion(
        Guid FromUserId,
        string FromUserName,
        Guid ToUserId,
        string ToUserName,
        long AmountCents);
    public record ExpenseSplitInfo(Guid UserId, long AmountOwedCents);
    public record Summary(int ExpenseCount, long TotalExpenseAmountCents, int SettlementCount, long TotalSettlementAmountCents);

    private sealed class BalanceNode
    {
        public BalanceNode(Guid userId, string displayName, long remainingCents)
        {
            UserId = userId;
            DisplayName = displayName;
            RemainingCents = remainingCents;
        }

        public Guid UserId { get; }
        public string DisplayName { get; }
        public long RemainingCents { get; set; }
    }
}
