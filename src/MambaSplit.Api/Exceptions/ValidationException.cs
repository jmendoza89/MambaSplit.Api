namespace MambaSplit.Api.Exceptions;

public class ValidationException : BusinessException
{
    public ValidationException(string message)
        : base("VALIDATION_FAILED", StatusCodes.Status400BadRequest, message)
    {
    }
}
