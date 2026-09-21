using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Service.Middleware;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Conflict"),
            OperationCanceledException => (0, (string?)null),
            // Any other exception type (HttpRequestException from an unreachable delivery endpoint,
            // TimeoutException, SocketException, etc.) still gets a real message instead of falling
            // through to ASP.NET Core's default handler, which returns a bare 500 with only a traceId.
            // This matters most for flow runs: an imported flow's delivery step failing against an
            // unreachable/misconfigured endpoint is one of the most likely first errors an operator
            // will hit, and it should be diagnosable from the API/UI response alone.
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error")
        };

        if (statusCode == 0 || title is null)
        {
            return false;
        }

        logger.LogWarning(exception, "Handled API exception with status code {StatusCode}.", statusCode);

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            // Surface the actual exception message rather than the generic status title. Callers (the
            // UI, API consumers, automated tooling) otherwise have no way to see *why* a request failed
            // (for example, which configuration reference is unresolved, or why HL7 parsing rejected a
            // payload) without tailing server logs.
            Detail = exception.Message,
            Instance = httpContext.Request.Path
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
