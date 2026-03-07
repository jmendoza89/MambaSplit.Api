using System.ComponentModel.DataAnnotations;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Validation;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MambaSplit.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("signup")]
    public async Task<ActionResult<AuthResponse>> Signup([FromBody] SignupRequest request, CancellationToken ct)
    {
        var user = await _authService.SignupAsync(request.Email, request.Password, request.DisplayName, ct);
        var tokens = await _authService.IssueTokensAsync(user, ct);
        return Ok(new AuthResponse(tokens.AccessToken, tokens.RefreshToken, UserDto.From(user)));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await _authService.AuthenticateAsync(request.Email, request.Password, ct);
        if (user is null)
        {
            throw new AuthenticationException("Invalid email or password");
        }

        var tokens = await _authService.IssueTokensAsync(user, ct);
        return Ok(new AuthResponse(tokens.AccessToken, tokens.RefreshToken, UserDto.From(user)));
    }

    [HttpPost("google")]
    public async Task<ActionResult<AuthResponse>> Google([FromBody] GoogleAuthRequest request, CancellationToken ct)
    {
        var user = await _authService.AuthenticateGoogleAsync(request.IdToken, ct);
        var tokens = await _authService.IssueTokensAsync(user, ct);
        return Ok(new AuthResponse(tokens.AccessToken, tokens.RefreshToken, UserDto.From(user)));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var tokens = await _authService.RefreshAsync(request.RefreshToken, ct);
        return Ok(new AuthResponse(tokens.AccessToken, tokens.RefreshToken, null));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        await _authService.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }
}

public record SignupRequest(
    [Required, NotBlank, EmailAddress, MaxLength(320)] string Email,
    [Required, NotBlank, StringLength(200, MinimumLength = 8)] string Password,
    [Required, NotBlank, MaxLength(120)] string DisplayName);

public record LoginRequest(
    [Required, NotBlank, EmailAddress] string Email,
    [Required, NotBlank] string Password);

public record GoogleAuthRequest([Required, NotBlank] string IdToken);
public record RefreshRequest([Required, NotBlank] string RefreshToken);
public record LogoutRequest([Required, NotBlank] string RefreshToken);

public record AuthResponse(string AccessToken, string RefreshToken, UserDto? User);
public record UserDto(string Id, string Email, string DisplayName)
{
    public static UserDto From(UserEntity user) => new(user.Id.ToString(), user.Email, user.DisplayName);
}
