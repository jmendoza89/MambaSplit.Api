using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using MambaSplit.Api.Extensions;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MambaSplit.Api.Configuration;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/internal/email")]
public class InternalEmailController : ControllerBase
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly TransactionalEmailService _emailService;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly EmailOptions _emailOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly Data.AppDbContext _db;

    public InternalEmailController(
        TransactionalEmailService emailService,
        IEmailTemplateRenderer templateRenderer,
        IOptions<EmailOptions> emailOptions,
        IWebHostEnvironment environment,
        Data.AppDbContext db)
    {
        _emailService = emailService;
        _templateRenderer = templateRenderer;
        _emailOptions = emailOptions.Value;
        _environment = environment;
        _db = db;
    }
    [HttpPost("send-release-v1.2.0")]
    public async Task<ActionResult> SendReleaseV120(CancellationToken ct)
    {
        EnsureInternalAccess();

        // Query all users
        var users = await _db.Users.AsNoTracking().ToListAsync(ct);
        if (users.Count == 0)
        {
            return Ok(new { sent = 0, message = "No users found." });
        }

        // Prepare static tokens
        var assetBaseUrl = $"{Request.Scheme}://{Request.Host}";
        var appLink = "https://ms.mambatech.io";
        var screenshotMain = $"{assetBaseUrl}/internal/email-preview-assets/screenshotMain.png";
        var screenshotGroup = $"{assetBaseUrl}/internal/email-preview-assets/screenshotGroup.png";

        int sent = 0;
        var errors = new List<string>();
        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Email)) continue;
            var firstName = !string.IsNullOrWhiteSpace(user.DisplayName)
                ? user.DisplayName.Split(' ', 2)[0]
                : user.Email.Split('@')[0];

            var model = new JsonObject
            {
                ["firstName"] = firstName,
                ["appLink"] = appLink,
                ["screenshotMain"] = screenshotMain,
                ["screenshotGroup"] = screenshotGroup
            };

            try
            {
                var result = await _emailService.SendTemplateAsync(
                    "release-v1.2.0",
                    new[] { user.Email },
                    null,
                    null,
                    null,
                    model,
                    null,
                    ct);
                if (result.Accepted)
                {
                    sent++;
                }
                else
                {
                    errors.Add($"{user.Email}: {result.ErrorMessage ?? result.ErrorCode}");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{user.Email}: {ex.Message}");
            }
        }

        return Ok(new { sent, total = users.Count, errors });
    }

    [AllowAnonymous]
    [HttpPost("render")]
    public ActionResult<InternalEmailRenderResponse> Render([FromBody] InternalEmailRenderRequest request)
    {
        if (!_environment.IsDevelopment())
        {
            EnsureInternalAccess();
        }

        var rendered = _templateRenderer.Render(request.TemplateKey, request.Model);
        var subject = string.IsNullOrWhiteSpace(request.Subject) ? rendered.Subject : request.Subject!;

        return Ok(new InternalEmailRenderResponse(
            subject,
            rendered.HtmlBody,
            rendered.TextBody));
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

public record InternalEmailRenderRequest(
    [Required, NotBlank] string TemplateKey,
    string? Subject,
    [Required] JsonObject Model);

public record InternalEmailSendResponse(
    bool Accepted,
    string Status,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage);

public record InternalEmailRenderResponse(
    string Subject,
    string HtmlBody,
    string TextBody);
