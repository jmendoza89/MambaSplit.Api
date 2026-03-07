using System.Security.Claims;
using MambaSplit.Api.Exceptions;

namespace MambaSplit.Api.Extensions;

public static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue("sub");
        if (!Guid.TryParse(value, out var userId))
        {
            throw new AuthenticationException("Invalid access token");
        }

        return userId;
    }

    public static string UserEmail(this ClaimsPrincipal principal)
    {
        var email = principal.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new AuthenticationException("Invalid access token");
        }

        return email;
    }
}
