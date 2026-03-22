using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/invites")]
public class InviteController : ControllerBase
{
    private readonly GroupService _groupService;

    public InviteController(GroupService groupService)
    {
        _groupService = groupService;
    }

    [HttpGet]
    public async Task<ActionResult<List<PendingInviteDto>>> ListPending(
        [FromQuery, Required, NotBlank, EmailAddress, MaxLength(320)] string email,
        CancellationToken ct)
    {
        var pending = await _groupService.ListPendingInvitesForEmailAsync(email, User.UserId(), ct);
        return Ok(pending.Select(PendingInviteDto.From).ToList());
    }

    [HttpPost("accept")]
    public async Task<IActionResult> Accept([FromBody] AcceptInviteRequest request, CancellationToken ct)
    {
        await _groupService.AcceptInviteAsync(request.Token, User.UserId(), ct);
        return Ok();
    }

    [HttpPost("decline")]
    public async Task<IActionResult> Decline([FromBody] DeclineInviteRequest request, CancellationToken ct)
    {
        await _groupService.DeclineInviteAsync(request.Token, User.UserId(), ct);
        return Ok();
    }

    [HttpPost("{inviteId}/accept")]
    public async Task<IActionResult> AcceptById(string inviteId, CancellationToken ct)
    {
        await _groupService.AcceptInviteByIdAsync(ParseGuid(inviteId, "inviteId"), User.UserId(), ct);
        return Ok();
    }

    [HttpPost("{inviteId}/decline")]
    public async Task<IActionResult> DeclineById(string inviteId, CancellationToken ct)
    {
        await _groupService.DeclineInviteByIdAsync(ParseGuid(inviteId, "inviteId"), User.UserId(), ct);
        return Ok();
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

public record AcceptInviteRequest([Required, NotBlank] string Token);
public record DeclineInviteRequest([Required, NotBlank] string Token);

public record PendingInviteDto(
    string Id,
    string GroupId,
    string GroupName,
    string SentByUserId,
    string SentByEmail,
    string SentByDisplayName,
    string SentToEmail,
    string ExpiresAt,
    string CreatedAt)
{
    public static PendingInviteDto From(GroupService.PendingInvite invite) => new(
        invite.Id.ToString(),
        invite.GroupId.ToString(),
        invite.GroupName,
        invite.SentByUserId.ToString(),
        invite.SentByEmail,
        invite.SentByDisplayName,
        invite.SentToEmail,
        invite.ExpiresAt.ToString("O"),
        invite.CreatedAt.ToString("O"));
}
