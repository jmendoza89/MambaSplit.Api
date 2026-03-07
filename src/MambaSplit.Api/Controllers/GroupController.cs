using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/groups")]
public class GroupController : ControllerBase
{
    private readonly GroupService _groupService;

    public GroupController(GroupService groupService)
    {
        _groupService = groupService;
    }

    [HttpPost]
    public async Task<ActionResult<GroupDto>> Create([FromBody] CreateGroupRequest request, CancellationToken ct)
    {
        var group = await _groupService.CreateGroupAsync(User.UserId(), request.Name, ct);
        return Ok(GroupDto.From(group));
    }

    [HttpGet]
    public async Task<ActionResult<List<GroupDto>>> List(CancellationToken ct)
    {
        var groups = await _groupService.ListGroupsForUserAsync(User.UserId(), ct);
        return Ok(groups.Select(GroupDto.From).ToList());
    }

    [HttpGet("{groupId}/details")]
    public async Task<ActionResult<GroupDetailsDto>> Details(string groupId, CancellationToken ct)
    {
        var details = await _groupService.GetGroupDetailsAsync(ParseGuid(groupId, "groupId"), User.UserId(), ct);
        return Ok(GroupDetailsDto.From(details));
    }

    [HttpDelete("{groupId}")]
    public async Task<IActionResult> Delete(string groupId, CancellationToken ct)
    {
        await _groupService.DeleteGroupAsync(ParseGuid(groupId, "groupId"), User.UserId(), ct);
        return NoContent();
    }

    [HttpPost("{groupId}/invites")]
    public async Task<ActionResult<InviteDto>> Invite(string groupId, [FromBody] InviteRequest request, CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        await _groupService.RequireMemberAsync(gid, User.UserId(), ct);
        var invite = await _groupService.CreateInviteAsync(gid, request.Email, ct);
        return Ok(new InviteDto(invite.Token, invite.Email, invite.ExpiresAt.ToString("O")));
    }

    [HttpDelete("{groupId}/invites/{token}")]
    public async Task<IActionResult> CancelInvite(string groupId, string token, CancellationToken ct)
    {
        await _groupService.CancelInviteAsync(ParseGuid(groupId, "groupId"), token, User.UserId(), ct);
        return NoContent();
    }

    private static Guid ParseGuid(string value, string field)
    {
        if (!Guid.TryParse(value, out var id))
        {
            throw new MambaSplit.Api.Exceptions.ValidationException($"{field}: must be a valid UUID");
        }

        return id;
    }
}

public record CreateGroupRequest([Required, NotBlank, MaxLength(200)] string Name);
public record GroupDto(string Id, string Name)
{
    public static GroupDto From(GroupEntity group) => new(group.Id.ToString(), group.Name);
}

public record InviteRequest([Required, NotBlank, EmailAddress, MaxLength(320)] string Email);
public record InviteDto(string Token, string Email, string ExpiresAt);

public record GroupDetailsDto(
    GroupInfoDto Group,
    MeInfoDto Me,
    List<MemberInfoDto> Members,
    List<ExpenseInfoDto> Expenses,
    SummaryDto Summary)
{
    public static GroupDetailsDto From(GroupService.GroupDetails details) => new(
        GroupInfoDto.From(details.Group),
        MeInfoDto.From(details.Me),
        details.Members.Select(MemberInfoDto.From).ToList(),
        details.Expenses.Select(ExpenseInfoDto.From).ToList(),
        SummaryDto.From(details.Summary));
}

public record GroupInfoDto(string Id, string Name, string CreatedBy, string CreatedAt)
{
    public static GroupInfoDto From(GroupService.GroupInfo group) =>
        new(group.Id.ToString(), group.Name, group.CreatedBy.ToString(), group.CreatedAt.ToString("O"));
}

public record MeInfoDto(string UserId, string Role, long NetBalanceCents)
{
    public static MeInfoDto From(GroupService.MeInfo me) =>
        new(me.UserId.ToString(), me.Role.ToString(), me.NetBalanceCents);
}

public record MemberInfoDto(string UserId, string DisplayName, string Email, string Role, string JoinedAt, long NetBalanceCents)
{
    public static MemberInfoDto From(GroupService.MemberInfo member) => new(
        member.UserId.ToString(),
        member.DisplayName,
        member.Email,
        member.Role.ToString(),
        member.JoinedAt.ToString("O"),
        member.NetBalanceCents);
}

public record ExpenseInfoDto(
    string Id,
    string Description,
    long AmountCents,
    string PayerUserId,
    string CreatedByUserId,
    string? ReversalOfExpenseId,
    string CreatedAt,
    List<ExpenseSplitInfoDto> Splits)
{
    public static ExpenseInfoDto From(GroupService.ExpenseInfo expense) => new(
        expense.Id.ToString(),
        expense.Description,
        expense.AmountCents,
        expense.PayerUserId.ToString(),
        expense.CreatedByUserId.ToString(),
        expense.ReversalOfExpenseId?.ToString(),
        expense.CreatedAt.ToString("O"),
        expense.Splits.Select(ExpenseSplitInfoDto.From).ToList());
}

public record ExpenseSplitInfoDto(string UserId, long AmountOwedCents)
{
    public static ExpenseSplitInfoDto From(GroupService.ExpenseSplitInfo split) =>
        new(split.UserId.ToString(), split.AmountOwedCents);
}

public record SummaryDto(int ExpenseCount, long TotalExpenseAmountCents)
{
    public static SummaryDto From(GroupService.Summary summary) =>
        new(summary.ExpenseCount, summary.TotalExpenseAmountCents);
}
