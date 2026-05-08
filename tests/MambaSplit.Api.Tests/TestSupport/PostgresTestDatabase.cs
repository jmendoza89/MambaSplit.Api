using MambaSplit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MambaSplit.Api.Tests.TestSupport;

internal sealed class PostgresTestDatabase : IDisposable
{
    private const string PostgresConnectionEnvironmentVariable = "MAMBASPLIT_TEST_POSTGRES_CONNECTION";
    private const string DefaultPostgresConnectionString = "Host=localhost;Port=5432;Database=mambasplit_test;Username=mambasplit;Password=mambasplit";

    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private readonly object _initLock = new();
    private bool _initialized;
    private bool _disposed;

    public string ConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
            {
                SearchPath = _schema,
            };
            return builder.ConnectionString;
        }
    }

    public void EnsureCreated()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            var connectionString = ConnectionString;
            var adminConnectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = string.Empty,
            };

            using var connection = new NpgsqlConnection(adminConnectionBuilder.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"drop schema if exists \"{_schema}\" cascade; create schema \"{_schema}\";";
            command.ExecuteNonQuery();

            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            using var db = new AppDbContext(dbOptions);
            var createScript = db.Database.GenerateCreateScript();

            using var createCommand = connection.CreateCommand();
            createCommand.CommandText = $"set search_path to \"{_schema}\"; {createScript}";
            createCommand.ExecuteNonQuery();

            _initialized = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var builder = new NpgsqlConnectionStringBuilder(GetBaseConnectionString())
        {
            SearchPath = string.Empty,
        };

        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"drop schema if exists \"{_schema}\" cascade;";
        command.ExecuteNonQuery();
    }

    private static string GetBaseConnectionString()
    {
        return Environment.GetEnvironmentVariable(PostgresConnectionEnvironmentVariable)
            ?? DefaultPostgresConnectionString;
    }
}
