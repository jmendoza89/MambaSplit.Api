using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/groups/{groupId}/expenses")]
public class ExpenseController : ControllerBase
{
    private readonly GroupService _groupService;
    private readonly ExpenseService _expenseService;

    public ExpenseController(GroupService groupService, ExpenseService expenseService)
    {
        _groupService = groupService;
        _expenseService = expenseService;
    }

    [HttpPost("equal")]
    public async Task<ActionResult<CreateExpenseResponse>> CreateEqual(
        string groupId,
        [FromBody] CreateEqualExpenseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        var actorUserId = User.UserId();
        await _groupService.RequireMemberAsync(gid, actorUserId, ct);
        var payer = ParseGuid(request.PayerUserId, "payerUserId");
        var participants = request.Participants.Select(p => ParseGuid(p, "participants")).ToList();
        var id = await _expenseService.CreateEqualSplitExpenseAsync(
            gid,
            actorUserId,
            payer,
            request.Description,
            request.AmountCents,
            participants,
            idempotencyKey,
            ct);

        return Ok(new CreateExpenseResponse(id.ToString()));
    }

    [HttpPost("exact")]
    public async Task<ActionResult<CreateExpenseResponse>> CreateExact(
        string groupId,
        [FromBody] CreateExactExpenseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        var actorUserId = User.UserId();
        await _groupService.RequireMemberAsync(gid, actorUserId, ct);
        var payer = ParseGuid(request.PayerUserId, "payerUserId");
        var items = request.Items.Select(i => new SplitExactItem(ParseGuid(i.UserId, "items.userId"), i.AmountCents)).ToList();
        var id = await _expenseService.CreateExactSplitExpenseAsync(
            gid,
            actorUserId,
            payer,
            request.Description,
            request.AmountCents,
            items,
            idempotencyKey,
            ct);

        return Ok(new CreateExpenseResponse(id.ToString()));
    }

    [HttpDelete("{expenseId}")]
    public async Task<IActionResult> Delete(string groupId, string expenseId, CancellationToken ct)
    {
        var gid = ParseGuid(groupId, "groupId");
        await _groupService.RequireMemberAsync(gid, User.UserId(), ct);
        await _expenseService.DeleteExpenseAsync(gid, ParseGuid(expenseId, "expenseId"), User.UserId(), ct);
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

public record CreateEqualExpenseRequest(
    [Required, NotBlank] string Description,
    [Required, NotBlank] string PayerUserId,
    [Range(1, long.MaxValue)] long AmountCents,
    [Required] List<string> Participants);

public record CreateExactExpenseRequest(
    [Required, NotBlank] string Description,
    [Required, NotBlank] string PayerUserId,
    [Range(1, long.MaxValue)] long AmountCents,
    [Required] List<CreateExactExpenseItem> Items);

public record CreateExactExpenseItem([Required, NotBlank] string UserId, long AmountCents);
public record CreateExpenseResponse(string ExpenseId);
