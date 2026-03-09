using MambaSplit.Api.Contracts;
using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Creates a new settlement (payment) between two users in a group.
    /// </summary>
    public async Task<Guid> CreateSettlementAsync(
        Guid groupId,
        Guid fromUserId,
        Guid toUserId,
        long amountCents,
        string? note = null,
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

        // Verify both users are members of the group
        await _groupService.RequireMembersAsync(groupId, new[] { fromUserId, toUserId }, ct);

        var settlement = new SettlementEntity
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            FromUserId = fromUserId,
            ToUserId = toUserId,
            AmountCents = amountCents,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Settlements.Add(settlement);
        await _db.SaveChangesAsync(ct);

        return settlement.Id;
    }

    /// <summary>
    /// Gets details of a specific settlement by ID.
    /// </summary>
    public async Task<SettlementDetailsResponse> GetSettlementAsync(Guid settlementId, CancellationToken ct = default)
    {
        var settlement = await _db.Settlements.FindAsync(new object[] { settlementId }, ct);
        if (settlement is null)
        {
            throw new ResourceNotFoundException("Settlement", settlementId.ToString());
        }

        return await BuildSettlementDetailsResponseAsync(settlement, ct);
    }

    /// <summary>
    /// Lists all settlements for a group, ordered by most recent first.
    /// </summary>
    public async Task<ListSettlementsResponse> ListGroupSettlementsAsync(
        Guid groupId,
        Guid userId,
        CancellationToken ct = default)
    {
        // Verify user is a member of the group
        await _groupService.RequireMemberAsync(groupId, userId, ct);

        var settlements = await _db.Settlements
            .Where(s => s.GroupId == groupId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var userIds = settlements
            .SelectMany(s => new[] { s.FromUserId, s.ToUserId })
            .Distinct()
            .ToList();

        var usersById = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var details = new List<SettlementDetailsResponse>();
        foreach (var settlement in settlements)
        {
            if (usersById.TryGetValue(settlement.FromUserId, out var fromUser) &&
                usersById.TryGetValue(settlement.ToUserId, out var toUser))
            {
                details.Add(MapToSettlementDetails(settlement, fromUser, toUser));
            }
        }

        return new ListSettlementsResponse { Settlements = details };
    }

    /// <summary>
    /// Lists all settlements involving a specific user across all groups.
    /// </summary>
    public async Task<ListSettlementsResponse> ListUserSettlementsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        // Verify user exists
        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        var settlements = await _db.Settlements
            .Where(s => s.FromUserId == userId || s.ToUserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var userIds = settlements
            .SelectMany(s => new[] { s.FromUserId, s.ToUserId })
            .Distinct()
            .ToList();

        var usersById = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var details = new List<SettlementDetailsResponse>();
        foreach (var settlement in settlements)
        {
            if (usersById.TryGetValue(settlement.FromUserId, out var fromUser) &&
                usersById.TryGetValue(settlement.ToUserId, out var toUser))
            {
                details.Add(MapToSettlementDetails(settlement, fromUser, toUser));
            }
        }

        return new ListSettlementsResponse { Settlements = details };
    }

    private async Task<SettlementDetailsResponse> BuildSettlementDetailsResponseAsync(
        SettlementEntity settlement,
        CancellationToken ct)
    {
        var fromUser = await _db.Users.FindAsync(new object[] { settlement.FromUserId }, ct);
        var toUser = await _db.Users.FindAsync(new object[] { settlement.ToUserId }, ct);

        if (fromUser is null || toUser is null)
        {
            throw new ResourceNotFoundException("Settlement", settlement.Id.ToString());
        }

        return MapToSettlementDetails(settlement, fromUser, toUser);
    }

    private static SettlementDetailsResponse MapToSettlementDetails(
        SettlementEntity settlement,
        UserEntity fromUser,
        UserEntity toUser)
    {
        return new SettlementDetailsResponse
        {
            Id = settlement.Id.ToString(),
            FromUserId = settlement.FromUserId.ToString(),
            FromUserName = fromUser.DisplayName,
            ToUserId = settlement.ToUserId.ToString(),
            ToUserName = toUser.DisplayName,
            AmountCents = settlement.AmountCents,
            Note = settlement.Note,
            CreatedAt = settlement.CreatedAt.ToString("O"),
        };
    }
}
