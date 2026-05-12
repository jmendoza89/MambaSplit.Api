using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class SettlementsController : ControllerBase
{
    private readonly SettlementService _settlementService;
    private readonly GroupService _groupService;

    public SettlementsController(SettlementService settlementService, GroupService groupService)
    {
        _settlementService = settlementService;
        _groupService = groupService;
    }

    [HttpPost("groups/{groupId}/settlements")]
    public async Task<ActionResult<CreateSettlementResponse>> CreateSettlement(
        string groupId,
        [FromBody] CreateSettlementRequest request,
        CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        var actorUserId = User.UserId();
        var fromUserId = ParseGuid(request.FromUserId, "fromUserId");
        var toUserId = ParseGuid(request.ToUserId, "toUserId");
        var settledAt = ParseDateTimeOffset(request.SettledAt, "settledAt");

        var created = await _settlementService.CreateSettlementAsync(
            gid,
            actorUserId,
            fromUserId,
            toUserId,
            request.AmountCents,
            request.Note,
            settledAt,
            ct);

        return CreatedAtAction(
            nameof(GetSettlement),
            new { settlementId = created.Id.ToString() },
            new CreateSettlementResponse(created.Id.ToString(), SettlementDetailsResponse.From(created)));
    }

    [HttpGet("groups/{groupId}/settlements")]
    public async Task<ActionResult<ListSettlementRowsResponse>> ListGroupSettlements(
        string groupId,
        [FromQuery] string? before,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        var userId = User.UserId();
        var response = await _groupService.ListSettlementsPageAsync(
            gid,
            userId,
            ParseDateTimeOffset(before, "before"),
            limit,
            ct);
        return Ok(ListSettlementRowsResponse.From(response));
    }

    [HttpGet("groups/{groupId}/settlements/{settlementId}/expenses")]
    public async Task<ActionResult<SettlementExpensesPageResponse>> ListSettlementExpenses(
        string groupId,
        string settlementId,
        [FromQuery] string? before,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var response = await _groupService.ListSettlementExpensesPageAsync(
            ParseGuid(groupId, "groupId"),
            ParseGuid(settlementId, "settlementId"),
            User.UserId(),
            ParseDateTimeOffset(before, "before"),
            limit,
            ct);

        return Ok(SettlementExpensesPageResponse.From(response));
    }

    [HttpGet("settlements/{settlementId}")]
    public async Task<ActionResult<SettlementDetailsResponse>> GetSettlement(
        string settlementId,
        CancellationToken ct)
    {
        var sid = ParseGuid(settlementId, "settlementId");
        var actorUserId = User.UserId();
        var response = await _settlementService.GetSettlementAsync(sid, actorUserId, ct);
        return Ok(SettlementDetailsResponse.From(response));
    }

    [HttpGet("users/{userId}/settlements")]
    public async Task<ActionResult<ListSettlementsResponse>> ListUserSettlements(
        string userId,
        CancellationToken ct)
    {
        var uid = ParseGuid(userId, "userId");
        var actorUserId = User.UserId();
        var response = await _settlementService.ListUserSettlementsAsync(uid, actorUserId, ct);
        return Ok(new ListSettlementsResponse(
            response.Settlements.Select(SettlementDetailsResponse.From).ToList()));
    }

    private static Guid ParseGuid(string input, string paramName)
    {
        if (!Guid.TryParse(input, out var guid))
        {
            throw new MambaSplit.Api.Exceptions.ValidationException($"{paramName} must be a valid GUID");
        }

        return guid;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, out var parsed))
        {
            throw new MambaSplit.Api.Exceptions.ValidationException($"{paramName} must be a valid ISO-8601 date/time");
        }

        return parsed;
    }
}

public record CreateSettlementRequest(
    [Required, NotBlank] string FromUserId,
    [Required, NotBlank] string ToUserId,
    [Range(1, long.MaxValue)] long AmountCents,
    [MaxLength(500)] string? Note,
    string? SettledAt);

public record CreateSettlementResponse(string SettlementId, SettlementDetailsResponse Settlement);

public record ListSettlementsResponse(List<SettlementDetailsResponse> Settlements);

public record SettlementDetailsResponse(
    string Id,
    string GroupId,
    string FromUserId,
    string FromUserName,
    string ToUserId,
    string ToUserName,
    long AmountCents,
    string? Note,
    string SettledAt,
    List<string> ExpenseIds)
{
    public static SettlementDetailsResponse From(SettlementService.SettlementDetails settlement) =>
        new(
            settlement.Id.ToString(),
            settlement.GroupId.ToString(),
            settlement.FromUserId.ToString(),
            settlement.FromUserName,
            settlement.ToUserId.ToString(),
            settlement.ToUserName,
            settlement.AmountCents,
            settlement.Note,
            settlement.SettledAt.ToString("O"),
            settlement.ExpenseIds.Select(id => id.ToString()).ToList());
}

public record ListSettlementRowsResponse(
    List<SettlementInfoDto> Settlements,
    bool HasMoreSettlements,
    string? NextBefore)
{
    public static ListSettlementRowsResponse From(GroupService.SettlementPage page) => new(
        page.Settlements.Select(SettlementInfoDto.From).ToList(),
        page.HasMoreSettlements,
        page.NextBefore?.ToString("O"));
}

public record SettlementExpensesPageResponse(
    SettlementInfoDto Settlement,
    List<ExpenseInfoDto> Expenses,
    bool HasMoreExpenses,
    string? NextBefore)
{
    public static SettlementExpensesPageResponse From(GroupService.SettlementExpensePage page) => new(
        SettlementInfoDto.From(page.Settlement),
        page.Expenses.Select(ExpenseInfoDto.From).ToList(),
        page.HasMoreExpenses,
        page.NextBefore?.ToString("O"));
}
