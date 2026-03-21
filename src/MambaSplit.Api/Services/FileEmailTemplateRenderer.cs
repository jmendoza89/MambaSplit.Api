using System.Text;
using System.Text.Json.Nodes;
using MambaSplit.Api.Configuration;
using MambaSplit.Api.Exceptions;
using Microsoft.Extensions.Options;

namespace MambaSplit.Api.Services;

public class FileEmailTemplateRenderer : IEmailTemplateRenderer
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private readonly IWebHostEnvironment _environment;
    private readonly EmailOptions _options;

    public FileEmailTemplateRenderer(IWebHostEnvironment environment, IOptions<EmailOptions> options)
    {
        _environment = environment;
        _options = options.Value;
    }

    public EmailTemplateRenderResult Render(string templateKey, JsonObject model)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            throw new ValidationException("templateKey is required");
        }

        var key = templateKey.Trim().ToLowerInvariant();
        var templateDirectory = Path.Combine(_environment.ContentRootPath, "Templates");
        var subjectPath = Path.Combine(templateDirectory, key + ".subject.txt");
        var htmlPath = Path.Combine(templateDirectory, key + ".html");
        var textPath = Path.Combine(templateDirectory, key + ".txt");

        if (!File.Exists(subjectPath) || !File.Exists(htmlPath) || !File.Exists(textPath))
        {
            throw new ValidationException($"templateKey '{templateKey}' is not supported");
        }

        var tokens = BuildTokens(key, model);
        var subject = ReplaceTokens(File.ReadAllText(subjectPath, Encoding.UTF8), tokens).Trim();
        var html = ReplaceTokens(File.ReadAllText(htmlPath, Encoding.UTF8), tokens);
        var text = ReplaceTokens(File.ReadAllText(textPath, Encoding.UTF8), tokens);

        return new EmailTemplateRenderResult(subject, html, text);
    }

    private Dictionary<string, string> BuildTokens(string templateKey, JsonObject model)
    {
        var tokens = model
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.GetValue<string>() ?? kvp.Value?.ToJsonString() ?? string.Empty,
                Comparer);

        if (Comparer.Equals(templateKey, "invite"))
        {
            var required = new[] { "groupName", "inviterName", "inviteToken" };
            foreach (var field in required)
            {
                if (!tokens.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ValidationException($"model.{field} is required for templateKey 'invite'");
                }
            }

            if (!tokens.ContainsKey("inviteLink"))
            {
                if (string.IsNullOrWhiteSpace(_options.FrontendBaseUrl))
                {
                    throw new ValidationException("Email:FrontendBaseUrl is required for invite template rendering");
                }

                var baseUrl = _options.FrontendBaseUrl.TrimEnd('/');
                var token = Uri.EscapeDataString(tokens["inviteToken"]);
                tokens["inviteLink"] = $"{baseUrl}/invite?token={token}";
            }

            if (!tokens.ContainsKey("inviteExpiresInText"))
            {
                tokens["inviteExpiresInText"] = "7 days left";
            }

            if (!tokens.ContainsKey("inviteExpiresAtTooltip"))
            {
                tokens["inviteExpiresAtTooltip"] = "Invitation expires in 7 days";
            }
        }

        if (Comparer.Equals(templateKey, "settlement"))
        {
            var required = new[] { "groupName", "payerName", "receiverName", "amountDisplay", "settledAtDisplay", "expenseCountText", "noteText" };
            foreach (var field in required)
            {
                if (!tokens.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ValidationException($"model.{field} is required for templateKey 'settlement'");
                }
            }

            EnsureGroupLinkToken(tokens, "settlement");
        }

        if (Comparer.Equals(templateKey, "invite-declined"))
        {
            var required = new[] { "groupName", "inviteeName", "inviteeEmail", "declinedAtDisplay" };
            foreach (var field in required)
            {
                if (!tokens.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ValidationException($"model.{field} is required for templateKey 'invite-declined'");
                }
            }

            EnsureGroupLinkToken(tokens, "invite-declined");
        }

        return tokens;
    }

    private void EnsureGroupLinkToken(IDictionary<string, string> tokens, string templateKey)
    {
        if (tokens.ContainsKey("groupLink"))
        {
            return;
        }

        if (!tokens.TryGetValue("groupId", out var groupId) || string.IsNullOrWhiteSpace(groupId))
        {
            throw new ValidationException($"model.groupId is required for templateKey '{templateKey}' when model.groupLink is not provided");
        }

        if (string.IsNullOrWhiteSpace(_options.FrontendBaseUrl))
        {
            throw new ValidationException($"Email:FrontendBaseUrl is required for {templateKey} template rendering");
        }

        var baseUrl = _options.FrontendBaseUrl.TrimEnd('/');
        var encodedGroupId = Uri.EscapeDataString(groupId);
        tokens["groupLink"] = $"{baseUrl}?groupId={encodedGroupId}";
    }

    private static string ReplaceTokens(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var output = template;
        foreach (var token in tokens)
        {
            output = output.Replace("{{" + token.Key + "}}", token.Value, StringComparison.OrdinalIgnoreCase);
        }

        return output;
    }
}
