using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException error) when (!context.Response.HasStarted)
        {
            int status = error.Kind switch
            {
                AppErrorKind.Validation => StatusCodes.Status400BadRequest,
                AppErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                AppErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                AppErrorKind.NotFound => StatusCodes.Status404NotFound,
                AppErrorKind.Conflict => StatusCodes.Status409Conflict,
                _ => throw new InvalidOperationException("Unrecognized application error kind.")
            };

            await Results.Problem(
                statusCode: status,
                detail: error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = error.Code })
                .ExecuteAsync(context);
        }
        catch (Exception error) when (!context.Response.HasStarted &&
            !(error is OperationCanceledException && context.RequestAborted.IsCancellationRequested))
        {
            logger.LogError(error, "Unexpected failure while handling an HTTP request.");
            await Results.Problem(statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
    }
}
