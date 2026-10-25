using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Teammy.Domain;

namespace Teammy.Api;

public sealed class WorkspaceExceptionHandler(IProblemDetailsService problems, ILogger<WorkspaceExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code, detail) = exception switch
        {
            WorkspaceException error => (error.Kind switch
            {
                WorkspaceErrorKind.Validation => StatusCodes.Status422UnprocessableEntity,
                WorkspaceErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                WorkspaceErrorKind.NotFound => StatusCodes.Status404NotFound,
                WorkspaceErrorKind.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError
            }, "Workspace action could not be completed", error.Code, error.Message),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request", "request.invalid", "The request body or parameters are invalid."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error", "server.error", "The request failed. Use the trace identifier when reporting this issue.")
        };

        if (status >= 500)
        {
            // Avoid exposing connection strings or tokens through exception messages/logging.
            logger.LogError("Request failed with {ExceptionType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        }

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
            }
        });
    }
}
