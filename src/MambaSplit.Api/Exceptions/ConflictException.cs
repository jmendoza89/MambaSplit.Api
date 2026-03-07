namespace MambaSplit.Api.Exceptions;

public class ConflictException : BusinessException
{
    public ConflictException(string message)
        : base("CONFLICT", StatusCodes.Status409Conflict, message)
    {
    }
}
