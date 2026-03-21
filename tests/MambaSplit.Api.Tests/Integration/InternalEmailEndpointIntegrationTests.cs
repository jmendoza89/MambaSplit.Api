using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MambaSplit.Api.Controllers;
using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MambaSplit.Api.Tests.Integration;

public class InternalEmailEndpointIntegrationTests
{
    [Fact]
    public async Task Render_Returns403_ForNonAdminOrNonInternalUser()
    {
        using var factory = new InternalEmailTestFactory(_ => EmailSendResult.Success("x"));
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "user@example.com");
        var response = await PostRender(client, token, "welcome", new { firstName = "Julio", appLink = "https://app.mambasplit.test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Render_Returns400_ForInvalidRequestPayload()
    {
        using var factory = new InternalEmailTestFactory(_ => EmailSendResult.Success("x"));
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "internal@example.com");
        var response = await PostJsonWithBearer(client, "/api/v1/internal/email/render", token, new
        {
            templateKey = "",
            model = new { firstName = "Julio" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_ReturnsRenderedBodies_WithoutSendingEmail()
    {
        var sendCallCount = 0;
        using var factory = new InternalEmailTestFactory(_ =>
        {
            sendCallCount++;
            return EmailSendResult.Success("provider-123");
        });
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "internal@example.com");
        var response = await PostRender(client, token, "welcome", new
        {
            firstName = "Julio",
            appLink = "https://app.mambasplit.test"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, sendCallCount);

        var body = await response.Content.ReadFromJsonAsync<InternalEmailRenderResponse>();
        Assert.NotNull(body);
        Assert.Equal("Welcome to MambaSplit, Julio", body!.Subject);
        Assert.Contains("Hi Julio, your account is ready.", body.HtmlBody);
        Assert.Contains("https://app.mambasplit.test", body.TextBody);
    }

    [Fact]
    public async Task Render_InviteDeclinedTemplate_ReturnsRenderedBodies_WithoutSendingEmail()
    {
        var sendCallCount = 0;
        using var factory = new InternalEmailTestFactory(_ =>
        {
            sendCallCount++;
            return EmailSendResult.Success("provider-123");
        });
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "internal@example.com");
        var response = await PostRender(client, token, "invite-declined", new
        {
            groupName = "Trip Budget",
            groupId = "11111111-1111-1111-1111-111111111111",
            inviteeName = "Ana",
            inviteeEmail = "ana@example.com",
            declinedAtDisplay = "March 18, 2026 at 8:00 PM UTC",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, sendCallCount);

        var body = await response.Content.ReadFromJsonAsync<InternalEmailRenderResponse>();
        Assert.NotNull(body);
        Assert.Contains("Trip Budget", body!.Subject);
        Assert.Contains("Ana", body.HtmlBody);
        Assert.Contains("ana@example.com", body.TextBody);
    }

    [Fact]
    public async Task Send_Returns403_ForNonAdminOrNonInternalUser()
    {
        using var factory = new InternalEmailTestFactory(_ => EmailSendResult.Success("x"));
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "user@example.com");
        var response = await PostSend(client, token, "invite", new[] { "to@example.com" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Send_Returns400_ForInvalidRequestPayload()
    {
        using var factory = new InternalEmailTestFactory(_ => EmailSendResult.Success("x"));
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "internal@example.com");
        var response = await PostJsonWithBearer(client, "/api/v1/internal/email/send", token, new
        {
            templateKey = "",
            to = Array.Empty<string>(),
            model = new { groupName = "Trip" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Send_ReturnsAcceptedTrue_WhenSenderSucceeds()
    {
        using var factory = new InternalEmailTestFactory(_ => EmailSendResult.Success("provider-123"));
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "internal@example.com");
        var response = await PostSend(client, token, "invite", new[] { "dest@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InternalEmailSendResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Accepted);
        Assert.Equal("accepted", body.Status);
        Assert.Equal("provider-123", body.ProviderMessageId);
    }

    [Fact]
    public async Task Send_ReturnsAcceptedFalse_WhenSenderFails()
    {
        using var factory = new InternalEmailTestFactory(_ => EmailSendResult.Failed("SMTP2GO_HTTP_400", "Bad payload"));
        using var client = factory.CreateClient();
        await EnsureDatabaseCreated(factory);

        var token = await Signup(client, "internal@example.com");
        var response = await PostSend(client, token, "invite", new[] { "dest@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InternalEmailSendResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Accepted);
        Assert.Equal("failed", body.Status);
        Assert.Equal("SMTP2GO_HTTP_400", body.ErrorCode);
    }

    private static async Task<HttpResponseMessage> PostSend(HttpClient client, string token, string templateKey, string[] to)
    {
        return await PostJsonWithBearer(client, "/api/v1/internal/email/send", token, new
        {
            templateKey,
            to,
            model = new
            {
                groupName = "Trip",
                inviterName = "Julio",
                inviteToken = "token-123",
                inviteExpiresInText = "7 days left",
                inviteExpiresAtTooltip = "March 24, 2026 at 8:00 PM UTC",
            },
            tags = new[] { "invite" },
        });
    }

    private static async Task<HttpResponseMessage> PostRender(HttpClient client, string token, string templateKey, object model)
    {
        return await PostJsonWithBearer(client, "/api/v1/internal/email/render", token, new
        {
            templateKey,
            model,
        });
    }

    private static async Task<HttpResponseMessage> PostJsonWithBearer(HttpClient client, string url, string token, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> Signup(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/signup", new
        {
            email,
            password = "password123",
            displayName = "Internal User",
        });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AuthPayload>();
        Assert.NotNull(payload);
        return payload!.AccessToken;
    }

    private static async Task EnsureDatabaseCreated(InternalEmailTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    private record AuthPayload(string AccessToken);

    private sealed class InternalEmailTestFactory : WebApplicationFactory<Program>
    {
        private readonly Func<EmailSendMessage, EmailSendResult> _resultFactory;
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mambasplit-email-tests-{Guid.NewGuid():N}.db");

        public InternalEmailTestFactory(Func<EmailSendMessage, EmailSendResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["app:security:jwt:issuer"] = "mambasplit-api-test",
                    ["app:security:jwt:secret"] = "test-secret-change-me-test-secret-change-me",
                    ["app:security:jwt:accessTokenMinutes"] = "15",
                    ["app:security:jwt:refreshTokenDays"] = "30",
                    ["app:database:runMigrationsOnStartup"] = "false",
                    ["ConnectionStrings:Default"] = "Host=ignored;Database=ignored;Username=ignored;Password=ignored",
                    ["Email:Provider"] = "smtp2go",
                    ["Email:ApiBaseUrl"] = "https://api.smtp2go.com/v3",
                    ["Email:ApiKey"] = "test-key",
                    ["Email:FromEmail"] = "mambasplit@mambatech.io",
                    ["Email:FromName"] = "MambaSplit",
                    ["Email:FrontendBaseUrl"] = "https://app.mambasplit.test",
                    ["Email:InternalAllowedEmails:0"] = "internal@example.com",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<IEmailSender>();

                services.AddDbContext<AppDbContext>((_, options) => options.UseSqlite($"Data Source={_databasePath}"));
                services.AddSingleton<IEmailSender>(new StubEmailSender(_resultFactory));
            });
        }
    }

    private sealed class StubEmailSender : IEmailSender
    {
        private readonly Func<EmailSendMessage, EmailSendResult> _resultFactory;

        public StubEmailSender(Func<EmailSendMessage, EmailSendResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public Task<EmailSendResult> SendAsync(EmailSendMessage message, CancellationToken ct = default)
        {
            return Task.FromResult(_resultFactory(message));
        }
    }
}
