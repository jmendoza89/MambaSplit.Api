namespace MambaSplit.Api.Exceptions;

public class AuthenticationException : BusinessException
{
    public AuthenticationException(string message)
        : base("AUTHENTICATION_FAILED", StatusCodes.Status401Unauthorized, message)
    {
    }
}
