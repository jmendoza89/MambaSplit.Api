using MambaSplit.Api.Configuration;
using MambaSplit.Api.Data;
using MambaSplit.Api.Security;
using MambaSplit.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace MambaSplit.Api.Tests.TestSupport;

internal sealed class AuthTestContext : IAsyncDisposable
{
    private readonly PostgresTestDatabase _database;
    public AppDbContext Db { get; }
    public Mock<IGoogleTokenVerifier> GoogleTokenVerifier { get; }
    public AuthService AuthService { get; }

    private AuthTestContext(
        PostgresTestDatabase database,
        AppDbContext db,
        Mock<IGoogleTokenVerifier> googleTokenVerifier,
        AuthService authService)
    {
        _database = database;
        Db = db;
        GoogleTokenVerifier = googleTokenVerifier;
        AuthService = authService;
    }

    public static async Task<AuthTestContext> CreateAsync()
    {
        var database = new PostgresTestDatabase();
        database.EnsureCreated();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        var db = new AppDbContext(dbOptions);

        var googleTokenVerifier = new Mock<IGoogleTokenVerifier>(MockBehavior.Strict);
        var securityOptions = Options.Create(new AppSecurityOptions
        {
            Jwt = new JwtOptions
            {
                Issuer = "test-issuer",
                Secret = "test-secret-key-with-32-characters",
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30,
            },
        });
        var jwtService = new JwtService(securityOptions);
        var authService = new AuthService(db, jwtService, securityOptions, googleTokenVerifier.Object);

        await Task.CompletedTask;
        return new AuthTestContext(database, db, googleTokenVerifier, authService);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        _database.Dispose();
    }
}
