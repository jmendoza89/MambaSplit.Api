using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Data;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/me")]
public class MeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GroupService _groupService;

    public MeController(AppDbContext db, GroupService groupService)
    {
        _db = db;
        _groupService = groupService;
    }

    [HttpGet]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var userId = User.UserId();
        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        return Ok(await BuildResponseAsync(user, ct));
    }

    [HttpPatch]
    public async Task<ActionResult<MeResponse>> Update([FromBody] UpdateMeRequest request, CancellationToken ct)
    {
        var userId = User.UserId();
        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        user.DisplayName = request.DisplayName;
        await _db.SaveChangesAsync(ct);
        return Ok(await BuildResponseAsync(user, ct));
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        [FromServices] AuthService authService,
        CancellationToken ct)
    {
        var userId = User.UserId();
        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", userId.ToString());
        }

        await authService.ChangePasswordAsync(user, request.NewPassword, request.CurrentPassword, ct);
        return NoContent();
    }

    private async Task<MeResponse> BuildResponseAsync(MambaSplit.Api.Domain.UserEntity user, CancellationToken ct)
    {
        var receivedInvites = await _groupService.ListPendingInvitesForEmailAsync(user.Email, user.Id, ct);
        var sentInvites = await _groupService.ListSentInvitesForUserAsync(user.Id, ct);

        return new MeResponse(
            user.Id.ToString(),
            user.Email,
            user.DisplayName,
            !string.IsNullOrWhiteSpace(user.GoogleSub),
            receivedInvites.Select(MeReceivedInviteDto.From).ToList(),
            sentInvites.Select(MeSentInviteDto.From).ToList());
    }
}

public record MeResponse(
    string Id,
    string Email,
    string DisplayName,
    bool HasGoogleLogin,
    List<MeReceivedInviteDto> ReceivedInvites,
    List<MeSentInviteDto> SentInvites);

public record ChangePasswordRequest(
    [MaxLength(200)] string? CurrentPassword,
    [Required, NotBlank, StringLength(200, MinimumLength = 8)] string NewPassword);

public record MeReceivedInviteDto(
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
    public static MeReceivedInviteDto From(GroupService.PendingInvite invite) => new(
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

public record MeSentInviteDto(
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
    public static MeSentInviteDto From(GroupService.SentInvite invite) => new(
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

public record UpdateMeRequest([Required, NotBlank, MaxLength(120)] string DisplayName);
