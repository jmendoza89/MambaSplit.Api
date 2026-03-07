using MambaSplit.Api.Configuration;
using MambaSplit.Api.Data;
using MambaSplit.Api.Security;
using MambaSplit.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace MambaSplit.Api.Tests.TestSupport;

internal sealed class AuthTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public AppDbContext Db { get; }
    public Mock<IGoogleTokenVerifier> GoogleTokenVerifier { get; }
    public AuthService AuthService { get; }

    private AuthTestContext(
        SqliteConnection connection,
        AppDbContext db,
        Mock<IGoogleTokenVerifier> googleTokenVerifier,
        AuthService authService)
    {
        _connection = connection;
        Db = db;
        GoogleTokenVerifier = googleTokenVerifier;
        AuthService = authService;
    }

    public static async Task<AuthTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

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

        return new AuthTestContext(connection, db, googleTokenVerifier, authService);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
