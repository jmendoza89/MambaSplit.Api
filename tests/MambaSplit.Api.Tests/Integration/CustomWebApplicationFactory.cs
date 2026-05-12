using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using MambaSplit.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MambaSplit.Api.Tests.Integration;

internal sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly PostgresTestDatabase _database = new();
    private readonly Func<EmailSendMessage, EmailSendResult>? _emailResultFactory;
    private readonly IList<EmailSendMessage>? _sentMessages;

    public CustomWebApplicationFactory(
        Func<EmailSendMessage, EmailSendResult>? emailResultFactory = null,
        IList<EmailSendMessage>? sentMessages = null)
    {
        _emailResultFactory = emailResultFactory;
        _sentMessages = sentMessages;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = _database.ConnectionString;
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["app:security:jwt:issuer"] = "mambasplit-api-test",
                ["app:security:jwt:secret"] = "test-secret-change-me-test-secret-change-me",
                ["app:security:jwt:accessTokenMinutes"] = "15",
                ["app:security:jwt:refreshTokenDays"] = "30",
                ["app:admin:portalToken"] = "test-admin-token",
                ["app:database:runMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Default"] = connectionString,
            };

            if (_emailResultFactory is not null)
            {
                settings["Email:Provider"] = "smtp2go";
                settings["Email:ApiBaseUrl"] = "https://api.smtp2go.com/v3";
                settings["Email:ApiKey"] = "test-key";
                settings["Email:FromEmail"] = "mambasplit@mambatech.io";
                settings["Email:FromName"] = "MambaSplit";
                settings["Email:FrontendBaseUrl"] = "https://app.mambasplit.test";
            }

            configBuilder.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();

            services.AddDbContext<AppDbContext>((_, options) =>
            {
                _database.EnsureCreated();
                options.UseNpgsql(connectionString);
            });

            if (_emailResultFactory is not null)
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new RecordingEmailSender(_emailResultFactory, _sentMessages));
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _database.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly Func<EmailSendMessage, EmailSendResult> _resultFactory;
        private readonly IList<EmailSendMessage>? _sentMessages;

        public RecordingEmailSender(
            Func<EmailSendMessage, EmailSendResult> resultFactory,
            IList<EmailSendMessage>? sentMessages)
        {
            _resultFactory = resultFactory;
            _sentMessages = sentMessages;
        }

        public Task<EmailSendResult> SendAsync(EmailSendMessage message, CancellationToken ct = default)
        {
            if (_sentMessages is not null)
            {
                lock (_sentMessages)
                {
                    _sentMessages.Add(message);
                }
            }

            return Task.FromResult(_resultFactory(message));
        }
    }
}
