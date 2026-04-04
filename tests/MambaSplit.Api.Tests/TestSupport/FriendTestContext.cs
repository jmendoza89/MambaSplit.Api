using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace MambaSplit.Api.Tests.TestSupport;

internal sealed class FriendTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public AppDbContext Db { get; }
    public FriendService FriendService { get; }

    private FriendTestContext(SqliteConnection connection, AppDbContext db, FriendService friendService)
    {
        _connection = connection;
        Db = db;
        FriendService = friendService;
    }

    public static async Task<FriendTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var logger = new Mock<ILogger<FriendService>>();
        var friendService = new FriendService(db, logger.Object);

        return new FriendTestContext(connection, db, friendService);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
