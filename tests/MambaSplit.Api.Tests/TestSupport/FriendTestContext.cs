using MambaSplit.Api.Data;
using MambaSplit.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace MambaSplit.Api.Tests.TestSupport;

internal sealed class FriendTestContext : IAsyncDisposable
{
    private readonly PostgresTestDatabase _database;
    public AppDbContext Db { get; }
    public FriendService FriendService { get; }

    private FriendTestContext(PostgresTestDatabase database, AppDbContext db, FriendService friendService)
    {
        _database = database;
        Db = db;
        FriendService = friendService;
    }

    public static async Task<FriendTestContext> CreateAsync()
    {
        var database = new PostgresTestDatabase();
        database.EnsureCreated();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        var db = new AppDbContext(dbOptions);

        var logger = new Mock<ILogger<FriendService>>();
        var friendService = new FriendService(db, logger.Object);

        await Task.CompletedTask;
        return new FriendTestContext(database, db, friendService);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        _database.Dispose();
    }
}
