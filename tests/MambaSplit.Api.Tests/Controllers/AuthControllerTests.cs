using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using MambaSplit.Api.Controllers;
using MambaSplit.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace MambaSplit.Api.Tests.Controllers;

public class AuthControllerTests
{
    [Fact]
    public async Task Google_ReturnsTokensAndUser()
    {
        await using var context = await AuthTestContext.CreateAsync();
        var controller = new AuthController(context.AuthService);
        context.GoogleTokenVerifier
            .Setup(v => v.VerifyAsync("id-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser("sub-1", "google@example.com", "Google User", null, true));

        var response = await controller.Google(new GoogleAuthRequest("id-token"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<AuthResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(payload.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(payload.RefreshToken));
        Assert.NotNull(payload.User);
        Assert.Equal("google@example.com", payload.User!.Email);
        Assert.Equal("Google User", payload.User.DisplayName);
        Assert.True(payload.User.HasGoogleLogin);
        context.GoogleTokenVerifier.VerifyAll();
    }

    [Fact]
    public async Task Login_InvalidCredentials_ThrowsAuthenticationException()
    {
        await using var context = await AuthTestContext.CreateAsync();
        var controller = new AuthController(context.AuthService);
        context.Db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-password"),
            DisplayName = "User",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<AuthenticationException>(
            () => controller.Login(new LoginRequest("user@example.com", "wrong-password"), CancellationToken.None));

        Assert.Equal("Invalid email or password", ex.Message);
    }
}
