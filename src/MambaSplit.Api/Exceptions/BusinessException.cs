namespace MambaSplit.Api.Exceptions;

public abstract class BusinessException : Exception
{
    protected BusinessException(string errorCode, int statusCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }
    public int StatusCode { get; }
}
