using MambaSplit.Api.Extensions;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/friends")]
public class FriendController : ControllerBase
{
    private readonly FriendService _friendService;

    public FriendController(FriendService friendService)
    {
        _friendService = friendService;
    }

    [HttpGet]
    public async Task<ActionResult<List<FriendListItemDto>>> List(CancellationToken ct)
    {
        var friends = await _friendService.ListForUserAsync(User.UserId(), ct);
        return Ok(friends.Select(FriendListItemDto.From).ToList());
    }

    [HttpGet("{friendConnectionId}")]
    public async Task<ActionResult<FriendDetailDto>> Detail(string friendConnectionId, CancellationToken ct)
    {
        if (!Guid.TryParse(friendConnectionId, out var id))
        {
            throw new MambaSplit.Api.Exceptions.ValidationException("friendConnectionId: must be a valid UUID");
        }

        var detail = await _friendService.GetDetailAsync(User.UserId(), id, ct);
        return Ok(FriendDetailDto.From(detail));
    }
}

public record FriendListItemDto(
    string Id,
    string DisplayName,
    string Email,
    string Status,
    string? FriendUserId,
    long NetBalanceCents,
    string NetBalanceLabel,
    int SharedGroupCount,
    bool HasActiveSharedBalances,
    string LastUsedAtUtc)
{
    public static FriendListItemDto From(FriendService.FriendListItem item) => new(
        item.Id.ToString(),
        item.DisplayName,
        item.Email,
        item.Status,
        item.FriendUserId,
        item.NetBalanceCents,
        item.NetBalanceLabel,
        item.SharedGroupCount,
        item.HasActiveSharedBalances,
        item.LastUsedAtUtc);
}

public record FriendDetailDto(
    string Id,
    string DisplayName,
    string Email,
    string Status,
    string? FriendUserId,
    long NetBalanceCents,
    string NetBalanceLabel,
    string Summary,
    List<SharedGroupBalanceDto> SharedGroups)
{
    public static FriendDetailDto From(FriendService.FriendDetail detail) => new(
        detail.Id.ToString(),
        detail.DisplayName,
        detail.Email,
        detail.Status,
        detail.FriendUserId,
        detail.NetBalanceCents,
        detail.NetBalanceLabel,
        detail.Summary,
        detail.SharedGroups.Select(SharedGroupBalanceDto.From).ToList());
}

public record SharedGroupBalanceDto(
    string GroupId,
    string GroupName,
    long BalanceCents,
    string BalanceLabel,
    bool HasUnsettledExpenses)
{
    public static SharedGroupBalanceDto From(FriendService.SharedGroupBalance g) => new(
        g.GroupId.ToString(),
        g.GroupName,
        g.BalanceCents,
        g.BalanceLabel,
        g.HasUnsettledExpenses);
}
