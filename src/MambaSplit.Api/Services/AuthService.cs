using MambaSplit.Api.Configuration;
using MambaSplit.Api.Data;
using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using BCryptNet = BCrypt.Net.BCrypt;

namespace MambaSplit.Api.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    private readonly JwtService _jwtService;
    private readonly JwtOptions _jwtOptions;
    private readonly IGoogleTokenVerifier _googleTokenVerifier;

    public AuthService(
        AppDbContext db,
        JwtService jwtService,
        IOptions<AppSecurityOptions> securityOptions,
        IGoogleTokenVerifier googleTokenVerifier)
    {
        _db = db;
        _jwtService = jwtService;
        _jwtOptions = securityOptions.Value.Jwt;
        _googleTokenVerifier = googleTokenVerifier;
    }

    public async Task<UserEntity> SignupAsync(string email, string rawPassword, string displayName, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (existing is not null)
        {
            throw new ConflictException("Email already in use: " + email);
        }

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            PasswordHash = BCryptNet.HashPassword(rawPassword),
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<UserEntity?> AuthenticateAsync(string email, string rawPassword, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (user is null)
        {
            return null;
        }

        return BCryptNet.Verify(rawPassword, user.PasswordHash) ? user : null;
    }

    public async Task<UserEntity> AuthenticateGoogleAsync(string idToken, CancellationToken ct = default)
    {
        var googleUser = await _googleTokenVerifier.VerifyAsync(idToken, ct);
        if (!googleUser.EmailVerified)
        {
            throw new AuthenticationException("Google email is not verified");
        }

        var existingBySub = await _db.Users.FirstOrDefaultAsync(u => u.GoogleSub == googleUser.Sub, ct);
        if (existingBySub is not null)
        {
            var updated = UpdateFromGoogle(existingBySub, googleUser);
            await _db.SaveChangesAsync(ct);
            return updated;
        }

        var normalizedEmail = googleUser.Email.Trim().ToLowerInvariant();
        var byEmail = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (byEmail is null)
        {
            var created = CreateGoogleUser(googleUser);
            _db.Users.Add(created);
            await _db.SaveChangesAsync(ct);
            return created;
        }

        if (string.IsNullOrWhiteSpace(byEmail.GoogleSub))
        {
            byEmail.GoogleSub = googleUser.Sub;
            UpdateFromGoogle(byEmail, googleUser);
            await _db.SaveChangesAsync(ct);
            return byEmail;
        }

        if (!string.Equals(byEmail.GoogleSub, googleUser.Sub, StringComparison.Ordinal))
        {
            throw new ConflictException("Email already linked to a different Google account");
        }

        var finalUser = UpdateFromGoogle(byEmail, googleUser);
        await _db.SaveChangesAsync(ct);
        return finalUser;
    }

    public async Task<Tokens> IssueTokensAsync(UserEntity user, CancellationToken ct = default)
    {
        var access = _jwtService.CreateAccessToken(user.Id, user.Email);
        var refresh = TokenCodec.RandomUrlToken(48);
        var refreshHash = TokenCodec.Sha256Base64Url(refresh);

        var token = new RefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
            RevokedAt = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
        return new Tokens(access, refresh);
    }

    public async Task<Tokens> RefreshAsync(string refreshTokenRaw, CancellationToken ct = default)
    {
        var hash = TokenCodec.Sha256Base64Url(refreshTokenRaw);
        var now = DateTimeOffset.UtcNow;
        var revoked = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"update refresh_tokens
               set revoked_at = {now}
             where token_hash = {hash}
               and revoked_at is null
               and expires_at > {now}", ct);

        if (revoked == 0)
        {
            throw new AuthenticationException("Invalid or expired refresh token");
        }

        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            throw new ResourceNotFoundException("RefreshToken", hash);
        }

        var user = await _db.Users.FindAsync(new object[] { token.UserId }, ct);
        if (user is null)
        {
            throw new ResourceNotFoundException("User", token.UserId.ToString());
        }

        return await IssueTokensAsync(user, ct);
    }

    public async Task LogoutAsync(string refreshTokenRaw, CancellationToken ct = default)
    {
        var hash = TokenCodec.Sha256Base64Url(refreshTokenRaw);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            return;
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static UserEntity UpdateFromGoogle(UserEntity user, GoogleUser googleUser)
    {
        if (string.IsNullOrWhiteSpace(user.DisplayName))
        {
            user.DisplayName = string.IsNullOrWhiteSpace(googleUser.Name) ? user.Email : googleUser.Name;
        }

        return user;
    }

    private static UserEntity CreateGoogleUser(GoogleUser googleUser)
    {
        var displayName = string.IsNullOrWhiteSpace(googleUser.Name) ? googleUser.Email : googleUser.Name;
        return new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = googleUser.Email.Trim().ToLowerInvariant(),
            PasswordHash = BCryptNet.HashPassword(TokenCodec.RandomUrlToken(48)),
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            GoogleSub = googleUser.Sub,
        };
    }

    public record Tokens(string AccessToken, string RefreshToken);
}
