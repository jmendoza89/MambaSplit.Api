using System.Text.Json.Nodes;
using MambaSplit.Api.Configuration;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace MambaSplit.Api.Tests.Services;

public class FileEmailTemplateRendererTests
{
    [Fact]
    public void Render_InviteTemplate_ReturnsSubjectHtmlAndText()
    {
        var environment = new StubWebHostEnvironment
        {
            ContentRootPath = FindApiContentRoot(),
        };
        var options = Options.Create(new EmailOptions
        {
            FrontendBaseUrl = "https://app.mambasplit.test",
        });
        var renderer = new FileEmailTemplateRenderer(environment, options);

        var model = new JsonObject
        {
            ["groupName"] = "Trip Budget",
            ["inviterName"] = "Julio",
            ["inviteToken"] = "token-123",
        };

        var result = renderer.Render("invite", model);

        Assert.Contains("Trip Budget", result.Subject);
        Assert.Contains("Julio", result.HtmlBody);
        Assert.Contains("https://app.mambasplit.test/invite?token=token-123", result.HtmlBody);
        Assert.Contains("Trip Budget", result.TextBody);
    }

    [Fact]
    public void Render_InviteTemplate_MissingRequiredField_ThrowsValidationException()
    {
        var environment = new StubWebHostEnvironment
        {
            ContentRootPath = FindApiContentRoot(),
        };
        var options = Options.Create(new EmailOptions
        {
            FrontendBaseUrl = "https://app.mambasplit.test",
        });
        var renderer = new FileEmailTemplateRenderer(environment, options);

        var model = new JsonObject
        {
            ["groupName"] = "Trip Budget",
            ["inviteToken"] = "token-123",
        };

        var ex = Assert.Throws<ValidationException>(() => renderer.Render("invite", model));
        Assert.Contains("model.inviterName", ex.Message);
    }

    private static string FindApiContentRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = Path.Combine(cursor.FullName, "src", "MambaSplit.Api", "Templates", "invite.html");
            if (File.Exists(candidate))
            {
                return Path.Combine(cursor.FullName, "src", "MambaSplit.Api");
            }

            cursor = cursor.Parent;
        }

        throw new InvalidOperationException("Unable to locate src/MambaSplit.Api content root from test base directory");
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MambaSplit.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
