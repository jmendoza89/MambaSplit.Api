using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace MambaSplit.Api.Tests.Integration;

internal sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mambasplit-tests-{Guid.NewGuid():N}.db");
    private readonly string _postgresSchema = $"test_{Guid.NewGuid():N}";
    private readonly object _postgresInitLock = new();
    private readonly Func<EmailSendMessage, EmailSendResult>? _emailResultFactory;
    private readonly IList<EmailSendMessage>? _sentMessages;
    private readonly TestDatabaseProvider _databaseProvider = TestDatabaseProviderSettings.GetProvider();
    private bool _postgresSchemaInitialized;

    public CustomWebApplicationFactory(
        Func<EmailSendMessage, EmailSendResult>? emailResultFactory = null,
        IList<EmailSendMessage>? sentMessages = null)
    {
        _emailResultFactory = emailResultFactory;
        _sentMessages = sentMessages;
    }

    public string DatabasePath => _databasePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = BuildConnectionString();
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
                if (_databaseProvider == TestDatabaseProvider.Postgres)
                {
                    EnsurePostgresSchemaInitialized(connectionString);
                    options.UseNpgsql(connectionString);
                }
                else
                {
                    options.UseSqlite(connectionString);
                }
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
        if (disposing && _databaseProvider == TestDatabaseProvider.Postgres)
        {
            DropPostgresSchema();
        }

        base.Dispose(disposing);
    }

    private string BuildConnectionString()
    {
        if (_databaseProvider == TestDatabaseProvider.Postgres)
        {
            var baseConnectionString = TestDatabaseProviderSettings.GetPostgresConnectionString();
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = _postgresSchema,
            };
            return builder.ConnectionString;
        }

        return $"Data Source={_databasePath}";
    }

    private void EnsurePostgresSchemaInitialized(string connectionString)
    {
        if (_postgresSchemaInitialized)
        {
            return;
        }

        lock (_postgresInitLock)
        {
            if (_postgresSchemaInitialized)
            {
                return;
            }

            EnsurePostgresSchema(connectionString);
            _postgresSchemaInitialized = true;
        }
    }

    private void EnsurePostgresSchema(string connectionString)
    {
        var adminConnectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = string.Empty,
        };
        using var connection = new NpgsqlConnection(adminConnectionBuilder.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"drop schema if exists \"{_postgresSchema}\" cascade; create schema \"{_postgresSchema}\";";
        command.ExecuteNonQuery();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        using var db = new AppDbContext(dbOptions);
        var createScript = db.Database.GenerateCreateScript();

        using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"set search_path to \"{_postgresSchema}\"; {createScript}";
        createCommand.ExecuteNonQuery();
    }

    private void DropPostgresSchema()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestDatabaseProviderSettings.GetPostgresConnectionString())
        {
            SearchPath = string.Empty,
        };

        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"drop schema if exists \"{_postgresSchema}\" cascade;";
        command.ExecuteNonQuery();
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

internal enum TestDatabaseProvider
{
    Sqlite,
    Postgres,
}

internal static class TestDatabaseProviderSettings
{
    private const string ProviderEnvironmentVariable = "MAMBASPLIT_TEST_DB_PROVIDER";
    private const string PostgresConnectionEnvironmentVariable = "MAMBASPLIT_TEST_POSTGRES_CONNECTION";
    private const string DefaultPostgresConnectionString = "Host=localhost;Port=5432;Database=mambasplit_test;Username=mambasplit;Password=mambasplit";

    public static TestDatabaseProvider GetProvider()
    {
        var rawValue = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable);
        return string.Equals(rawValue, "postgres", StringComparison.OrdinalIgnoreCase)
            ? TestDatabaseProvider.Postgres
            : TestDatabaseProvider.Sqlite;
    }

    public static string GetPostgresConnectionString()
    {
        return Environment.GetEnvironmentVariable(PostgresConnectionEnvironmentVariable)
            ?? DefaultPostgresConnectionString;
    }
}
