using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MambaSplit.Api.Configuration;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/internal/email")]
public class InternalEmailController : ControllerBase
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly TransactionalEmailService _emailService;
    private readonly EmailOptions _emailOptions;

    public InternalEmailController(TransactionalEmailService emailService, IOptions<EmailOptions> emailOptions)
    {
        _emailService = emailService;
        _emailOptions = emailOptions.Value;
    }

    [HttpPost("send")]
    public async Task<ActionResult<InternalEmailSendResponse>> Send(
        [FromBody] InternalEmailSendRequest request,
        CancellationToken ct)
    {
        EnsureInternalAccess();

        var to = NormalizeRecipients(request.To, "to", required: true);
        var cc = NormalizeRecipients(request.Cc, "cc", required: false);
        var bcc = NormalizeRecipients(request.Bcc, "bcc", required: false);

        var result = await _emailService.SendTemplateAsync(
            request.TemplateKey,
            to,
            cc,
            bcc,
            request.Subject,
            request.Model,
            request.Tags,
            ct);

        return Ok(new InternalEmailSendResponse(
            result.Accepted,
            result.Status,
            result.ProviderMessageId,
            result.ErrorCode,
            result.ErrorMessage));
    }

    private void EnsureInternalAccess()
    {
        var roleClaim = User.FindFirst("role")?.Value;
        if (string.Equals(roleClaim, "admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(roleClaim, "internal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var email = User.UserEmail();
        if (_emailOptions.InternalAllowedEmails.Any(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new AuthorizationException("internal email send");
    }

    private static List<string> NormalizeRecipients(IReadOnlyList<string>? values, string field, bool required)
    {
        var normalized = (values ?? [])
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (required && normalized.Count == 0)
        {
            throw new MambaSplit.Api.Exceptions.ValidationException($"{field} must contain at least one email");
        }

        if (normalized.Count > 100)
        {
            throw new MambaSplit.Api.Exceptions.ValidationException($"{field} must contain at most 100 emails");
        }

        foreach (var value in normalized)
        {
            if (!EmailValidator.IsValid(value))
            {
                throw new MambaSplit.Api.Exceptions.ValidationException($"{field} contains an invalid email address");
            }
        }

        return normalized;
    }
}

public record InternalEmailSendRequest(
    [Required, NotBlank] string TemplateKey,
    [Required] IReadOnlyList<string> To,
    IReadOnlyList<string>? Cc,
    IReadOnlyList<string>? Bcc,
    string? Subject,
    [Required] JsonObject Model,
    IReadOnlyList<string>? Tags);

public record InternalEmailSendResponse(
    bool Accepted,
    string Status,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage);
