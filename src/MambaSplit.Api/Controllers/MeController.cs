using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Data;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/me")]
public class MeController : ControllerBase
{
    private readonly AppDbContext _db;

    public MeController(AppDbContext db)
    {
        _db = db;
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

        return Ok(new MeResponse(user.Id.ToString(), user.Email, user.DisplayName));
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
        return Ok(new MeResponse(user.Id.ToString(), user.Email, user.DisplayName));
    }
}

public record MeResponse(string Id, string Email, string DisplayName);
public record UpdateMeRequest([Required, NotBlank, MaxLength(120)] string DisplayName);
