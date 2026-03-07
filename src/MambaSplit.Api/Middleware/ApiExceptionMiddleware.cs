using MambaSplit.Api.Contracts;
using MambaSplit.Api.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Middleware;

public class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BusinessException ex)
        {
            await WriteError(context, ex.StatusCode, ex.ErrorCode, ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "INVALID_REQUEST", ex.Message);
        }
        catch (FormatException ex)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "INVALID_REQUEST", ex.Message);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Data integrity violation");
            await WriteError(
                context,
                StatusCodes.Status409Conflict,
                "DATA_INTEGRITY_VIOLATION",
                "Resource conflicts with an existing record.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled API error");
            await WriteError(
                context,
                StatusCodes.Status500InternalServerError,
                "DATA_ACCESS_ERROR",
                "A data access error occurred.");
        }
    }

    private static async Task WriteError(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ErrorResponse(code, message, DateTimeOffset.UtcNow.ToString("O")));
    }
}
