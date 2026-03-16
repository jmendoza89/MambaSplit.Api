using System.Text.Json.Nodes;

namespace MambaSplit.Api.Services;

public interface IEmailTemplateRenderer
{
    EmailTemplateRenderResult Render(string templateKey, JsonObject model);
}

public record EmailTemplateRenderResult(string Subject, string HtmlBody, string TextBody);
