using MambaSplit.Api.Data;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;

    public UsersController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<UserLookupDto>>> Search(
        [FromQuery] string? q,
        [FromQuery] string? groupId,
        CancellationToken ct)
    {
        var actorUserId = User.UserId();
        Guid? groupGuid = null;
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            groupGuid = ParseGuid(groupId, "groupId");
            var isMember = await _db.GroupMembers
                .AnyAsync(gm => gm.GroupId == groupGuid.Value && gm.UserId == actorUserId, ct);
            if (!isMember)
            {
                throw new AuthorizationException("access", "group " + groupGuid.Value);
            }
        }

        var normalized = q?.Trim().ToLowerInvariant() ?? string.Empty;
        var query = _db.Users
            .AsNoTracking()
            .Where(u => u.Id != actorUserId);

        if (groupGuid is not null)
        {
            var memberIds = _db.GroupMembers
                .Where(gm => gm.GroupId == groupGuid.Value)
                .Select(gm => gm.UserId);
            query = query.Where(u => !memberIds.Contains(u.Id));
        }

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            query = query.Where(u =>
                u.DisplayName.ToLower().Contains(normalized) ||
                u.Email.ToLower().Contains(normalized));
        }

        var users = await query
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Email)
            .Take(50)
            .Select(u => new UserLookupDto(
                u.Id.ToString(),
                u.DisplayName,
                u.Email))
            .ToListAsync(ct);

        return Ok(users);
    }

    private static Guid ParseGuid(string value, string field)
    {
        if (!Guid.TryParse(value, out var parsed))
        {
            throw new ValidationException($"{field}: must be a valid UUID");
        }

        return parsed;
    }
}

public record UserLookupDto(string Id, string DisplayName, string Email);
