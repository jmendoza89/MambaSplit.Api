using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MambaSplit.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MambaSplit.Api.Security;

public class JwtService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    public JwtService(IOptions<AppSecurityOptions> options)
    {
        _options = options.Value.Jwt;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public string CreateAccessToken(Guid userId, string email)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("email", email),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: null,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
