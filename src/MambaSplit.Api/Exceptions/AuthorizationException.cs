namespace MambaSplit.Api.Exceptions;

public class AuthorizationException : BusinessException
{
    public AuthorizationException(string message)
        : base("FORBIDDEN", StatusCodes.Status403Forbidden, message)
    {
    }

    public AuthorizationException(string action, string resource)
        : base("FORBIDDEN", StatusCodes.Status403Forbidden, $"Not authorized to {action} {resource}")
    {
    }
}
