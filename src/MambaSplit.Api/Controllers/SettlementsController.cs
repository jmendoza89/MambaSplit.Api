using MambaSplit.Api.Contracts;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class SettlementsController : ControllerBase
{
    private readonly SettlementService _settlementService;

    public SettlementsController(SettlementService settlementService)
    {
        _settlementService = settlementService;
    }

    /// <summary>
    /// Creates a new settlement (payment between users) in a group.
    /// </summary>
    [HttpPost("groups/{groupId}/settlements")]
    public async Task<ActionResult<CreateSettlementResponse>> CreateSettlement(
        string groupId,
        [FromBody] CreateSettlementRequest request,
        CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        var fromUserId = ParseGuid(request.FromUserId, "fromUserId");
        var toUserId = ParseGuid(request.ToUserId, "toUserId");

        var settlementId = await _settlementService.CreateSettlementAsync(
            gid,
            fromUserId,
            toUserId,
            request.AmountCents,
            request.Note,
            ct);

        return CreatedAtAction(
            nameof(GetSettlement),
            new { settlementId = settlementId.ToString() },
            new CreateSettlementResponse { SettlementId = settlementId.ToString() });
    }

    /// <summary>
    /// Lists all settlements for a group (requires group membership).
    /// </summary>
    [HttpGet("groups/{groupId}/settlements")]
    public async Task<ActionResult<ListSettlementsResponse>> ListGroupSettlements(
        string groupId,
        CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        var userId = User.UserId();

        var response = await _settlementService.ListGroupSettlementsAsync(gid, userId, ct);
        return Ok(response);
    }

    /// <summary>
    /// Gets details of a specific settlement by ID.
    /// </summary>
    [HttpGet("settlements/{settlementId}")]
    public async Task<ActionResult<SettlementDetailsResponse>> GetSettlement(
        string settlementId,
        CancellationToken ct)
    {
        var sid = ParseGuid(settlementId, "settlementId");
        var response = await _settlementService.GetSettlementAsync(sid, ct);
        return Ok(response);
    }

    /// <summary>
    /// Lists all settlements involving the authenticated user.
    /// </summary>
    [HttpGet("users/{userId}/settlements")]
    public async Task<ActionResult<ListSettlementsResponse>> ListUserSettlements(
        string userId,
        CancellationToken ct)
    {
        var uid = ParseGuid(userId, "userId");
        var response = await _settlementService.ListUserSettlementsAsync(uid, ct);
        return Ok(response);
    }

    private static Guid ParseGuid(string input, string paramName)
    {
        if (!Guid.TryParse(input, out var guid))
        {
            throw new ValidationException($"{paramName} must be a valid GUID");
        }
        return guid;
    }
}
