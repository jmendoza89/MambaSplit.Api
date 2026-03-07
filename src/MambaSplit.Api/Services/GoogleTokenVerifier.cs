namespace MambaSplit.Api.Services;

public interface IGoogleTokenVerifier
{
    Task<GoogleUser> VerifyAsync(string idToken, CancellationToken ct = default);
}

public record GoogleUser(string Sub, string Email, string? Name, string? Picture, bool EmailVerified);
