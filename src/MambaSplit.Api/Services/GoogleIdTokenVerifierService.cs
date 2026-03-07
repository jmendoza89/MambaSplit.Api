using Google.Apis.Auth;
using MambaSplit.Api.Configuration;
using MambaSplit.Api.Exceptions;
using Microsoft.Extensions.Options;

namespace MambaSplit.Api.Services;

public class GoogleIdTokenVerifierService : IGoogleTokenVerifier
{
    private readonly string _clientId;

    public GoogleIdTokenVerifierService(IOptions<AppSecurityOptions> options, IConfiguration configuration)
    {
        var configured = options.Value.Google.ClientId?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = configuration["app:security:google:client-id"]?.Trim();
        }

        _clientId = configured ?? string.Empty;
    }

    public async Task<GoogleUser> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new ValidationException("Google authentication is not configured");
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { _clientId },
                });
        }
        catch (Exception ex)
        {
            throw new AuthenticationException("Failed to verify Google ID token: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(payload.Subject) || string.IsNullOrWhiteSpace(payload.Email))
        {
            throw new AuthenticationException("Invalid Google token payload: missing sub or email");
        }

        return new GoogleUser(
            payload.Subject,
            payload.Email,
            payload.Name,
            payload.Picture,
            payload.EmailVerified);
    }
}
