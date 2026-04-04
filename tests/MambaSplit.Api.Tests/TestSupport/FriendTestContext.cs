using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;

namespace MambaSplit.Api.Tests.TestSupport;

/// <summary>
/// Creates a real PostgreSQL-backed test context using a unique schema per test
/// so tests are isolated and run against production-equivalent infrastructure.
/// </summary>
internal sealed class FriendTestContext : IAsyncDisposable
{
    private const string BaseConnectionString =
        "Host=localhost;Port=5432;Database=mambasplit;Username=mambasplit;Password=mambasplit;Include Error Detail=true";

    private readonly string _schema;
    public AppDbContext Db { get; }
    public FriendService FriendService { get; }

    private FriendTestContext(string schema, AppDbContext db, FriendService friendService)
    {
        _schema = schema;
        Db = db;
        FriendService = friendService;
    }

    public static async Task<FriendTestContext> CreateAsync()
    {
        // Each test gets a unique schema so tests never collide.
        var schema = "test_" + Guid.NewGuid().ToString("N")[..12];

        // Create the schema using a raw connection.
        await using (var conn = new NpgsqlConnection(BaseConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA \"{schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        // Connect with search_path scoped to ONLY the test schema.
        // Excluding 'public' ensures EF doesn't see production tables
        // and CreateTablesAsync creates everything in the test schema.
        var testConnStr = $"{BaseConnectionString};Search Path={schema}";

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(testConnStr)
            .Options;

        var db = new AppDbContext(dbOptions);

        // Force-create all model tables in the test schema.
        // EnsureCreatedAsync() checks across ALL schemas and skips creation
        // when public.* tables exist; CreateTablesAsync() always emits DDL.
        var creator = ((IInfrastructure<IServiceProvider>)db).Instance
            .GetRequiredService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync();

        var logger = new Mock<ILogger<FriendService>>();
        var friendService = new FriendService(db, logger.Object);

        return new FriendTestContext(schema, db, friendService);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();

        // Drop the entire schema and all objects created during the test.
        await using var conn = new NpgsqlConnection(BaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA \"{_schema}\" CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }
}
