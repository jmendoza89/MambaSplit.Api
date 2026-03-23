using System.Text.Json.Nodes;
using MambaSplit.Api.Configuration;
using Microsoft.Extensions.Options;

namespace MambaSplit.Api.Services;

public class TransactionalEmailService
{
    private readonly IEmailSender _emailSender;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly EmailOptions _options;
    private readonly ILogger<TransactionalEmailService> _logger;

    public TransactionalEmailService(
        IEmailSender emailSender,
        IEmailTemplateRenderer templateRenderer,
        IOptions<EmailOptions> options,
        ILogger<TransactionalEmailService> logger)
    {
        _emailSender = emailSender;
        _templateRenderer = templateRenderer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendTemplateAsync(
        string templateKey,
        IReadOnlyList<string> to,
        IReadOnlyList<string>? cc,
        IReadOnlyList<string>? bcc,
        string? subject,
        JsonObject model,
        IReadOnlyList<string>? tags,
        CancellationToken ct = default)
    {
        if (!string.Equals(_options.Provider, "smtp2go", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Transactional email provider is disabled; skipping template send for {TemplateKey}", templateKey);
            return EmailSendResult.Failed("EMAIL_PROVIDER_DISABLED", "Transactional email provider is disabled");
        }

        var rendered = _templateRenderer.Render(templateKey, model);
        var result = await _emailSender.SendAsync(
            new EmailSendMessage(
                to,
                cc ?? [],
                bcc ?? [],
                string.IsNullOrWhiteSpace(subject) ? rendered.Subject : subject!,
                rendered.HtmlBody,
                rendered.TextBody,
                tags ?? []),
            ct);

        _logger.LogInformation(
            "Transactional email send result template={TemplateKey} accepted={Accepted} status={Status} providerMessageId={ProviderMessageId} errorCode={ErrorCode}",
            templateKey,
            result.Accepted,
            result.Status,
            result.ProviderMessageId,
            result.ErrorCode);

        return result;
    }
}
