using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace MambaSplit.Api.Tests.Services;

public class AuthServiceTests
{
    [Fact]
    public async Task AuthenticateGoogleAsync_ExistingByGoogleSub_ReturnsExistingUser()
    {
        await using var context = await AuthTestContext.CreateAsync();
        var existing = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = "hash",
            DisplayName = "Existing User",
            GoogleSub = "sub-1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Db.Users.Add(existing);
        await context.Db.SaveChangesAsync();

        context.GoogleTokenVerifier
            .Setup(v => v.VerifyAsync("id-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser("sub-1", "user@example.com", "Google User", null, true));

        var result = await context.AuthService.AuthenticateGoogleAsync("id-token");

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(1, await context.Db.Users.CountAsync());
        context.GoogleTokenVerifier.VerifyAll();
    }

    [Fact]
    public async Task AuthenticateGoogleAsync_LinkByEmailWhenGoogleSubMissing_UpdatesUser()
    {
        await using var context = await AuthTestContext.CreateAsync();
        var existing = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = "hash",
            DisplayName = "Existing User",
            GoogleSub = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Db.Users.Add(existing);
        await context.Db.SaveChangesAsync();

        context.GoogleTokenVerifier
            .Setup(v => v.VerifyAsync("id-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser("sub-2", "user@example.com", "Google User", null, true));

        var result = await context.AuthService.AuthenticateGoogleAsync("id-token");

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal("sub-2", result.GoogleSub);
        Assert.Equal(1, await context.Db.Users.CountAsync());
        context.GoogleTokenVerifier.VerifyAll();
    }

    [Fact]
    public async Task AuthenticateGoogleAsync_CreatesNewUserWhenNoMatches()
    {
        await using var context = await AuthTestContext.CreateAsync();
        context.GoogleTokenVerifier
            .Setup(v => v.VerifyAsync("id-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser("sub-3", "new@example.com", "New User", null, true));

        var result = await context.AuthService.AuthenticateGoogleAsync("id-token");

        Assert.Equal("new@example.com", result.Email);
        Assert.Equal("sub-3", result.GoogleSub);
        Assert.Equal("New User", result.DisplayName);
        Assert.NotEmpty(result.PasswordHash);
        Assert.Single(await context.Db.Users.ToListAsync());
        context.GoogleTokenVerifier.VerifyAll();
    }

    [Fact]
    public async Task AuthenticateGoogleAsync_ConflictingGoogleSubForEmail_ThrowsConflict()
    {
        await using var context = await AuthTestContext.CreateAsync();
        context.Db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = "hash",
            DisplayName = "Existing User",
            GoogleSub = "other-sub",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.Db.SaveChangesAsync();

        context.GoogleTokenVerifier
            .Setup(v => v.VerifyAsync("id-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser("sub-4", "user@example.com", "Google User", null, true));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => context.AuthService.AuthenticateGoogleAsync("id-token"));

        Assert.Equal("Email already linked to a different Google account", ex.Message);
        context.GoogleTokenVerifier.VerifyAll();
    }

    [Fact]
    public async Task AuthenticateGoogleAsync_UnverifiedEmail_ThrowsAuthenticationException()
    {
        await using var context = await AuthTestContext.CreateAsync();
        context.GoogleTokenVerifier
            .Setup(v => v.VerifyAsync("id-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser("sub-5", "user@example.com", "Google User", null, false));

        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => context.AuthService.AuthenticateGoogleAsync("id-token"));

        Assert.Equal("Google email is not verified", ex.Message);
        context.GoogleTokenVerifier.VerifyAll();
    }

    [Fact]
    public async Task SignupAsync_DuplicateEmailIgnoringCase_ThrowsConflict()
    {
        await using var context = await AuthTestContext.CreateAsync();
        context.Db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = "hash",
            DisplayName = "Existing User",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => context.AuthService.SignupAsync("USER@example.com", "password123", "New User"));

        Assert.Equal("Email already in use: USER@example.com", ex.Message);
    }
}
