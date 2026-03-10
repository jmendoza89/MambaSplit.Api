using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/admin/groups/{groupId}/settlements")]
public class AdminSettlementsController : ControllerBase
{
    private readonly SettlementService _settlementService;
    private readonly IConfiguration _configuration;

    public AdminSettlementsController(SettlementService settlementService, IConfiguration configuration)
    {
        _settlementService = settlementService;
        _configuration = configuration;
    }

    [HttpDelete]
    public async Task<ActionResult<AdminResetSettlementsResponse>> ResetGroupSettlements(
        string groupId,
        [FromHeader(Name = "X-Admin-Portal-Token")] string? adminPortalToken,
        CancellationToken ct)
    {
        ValidateAdminPortalToken(adminPortalToken);
        var gid = ParseGuid(groupId, "groupId");
        var result = await _settlementService.ResetGroupSettlementsAsync(gid, ct);
        return Ok(new AdminResetSettlementsResponse(
            result.GroupId.ToString(),
            result.DeletedSettlementCount,
            result.ReleasedExpenseCount));
    }

    private void ValidateAdminPortalToken(string? providedToken)
    {
        var configuredToken = _configuration["app:admin:portalToken"];
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            throw new AuthorizationException("admin settlement reset is not enabled");
        }

        if (!string.Equals(configuredToken, providedToken, StringComparison.Ordinal))
        {
            throw new AuthorizationException("admin settlement reset");
        }
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

public record AdminResetSettlementsResponse(
    string GroupId,
    int DeletedSettlementCount,
    int ReleasedExpenseCount);
