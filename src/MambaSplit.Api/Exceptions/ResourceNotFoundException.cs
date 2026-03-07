namespace MambaSplit.Api.Exceptions;

public class ResourceNotFoundException : BusinessException
{
    public ResourceNotFoundException(string resourceType, string identifier)
        : base("RESOURCE_NOT_FOUND", StatusCodes.Status404NotFound, $"{resourceType} not found: {identifier}")
    {
    }

    public ResourceNotFoundException(string message)
        : base("RESOURCE_NOT_FOUND", StatusCodes.Status404NotFound, message)
    {
    }
}
