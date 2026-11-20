using System.Net;
using System.Security.Principal;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Security_Camera_Backend.Exceptions;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title) = MapException(exception);

        LogProblemDetails(statusCode, exception, httpContext, logger);

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = GetSafeErrorMessage(exception, httpContext),
            Instance = httpContext.Request.Path
        };

        // problemDetails.Extensions["traceID"] = httpContext.TraceIdentifier;
        problemDetails.Extensions["TimeUTC"] = DateTime.UtcNow;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext()
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });
    }

    private static (int statusCode, string title) MapException(Exception exception) => exception switch
    {
        AppException appException => ((int)appException.StatusCode, appException.PublicMessage),
        NotImplementedException notImplementedException => ((int)HttpStatusCode.NotImplemented, "Not Implemented Yet"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
        UnauthorizedAccessException => (StatusCodes.Status404NotFound, "Not Found"),
        MethodAccessException  => (StatusCodes.Status405MethodNotAllowed, "Forbidden"),
        _ => (StatusCodes.Status500InternalServerError, "An internal error occured")
    };


    private static string? GetSafeErrorMessage(Exception exception, HttpContext context)
    {
        var env = context.RequestServices.GetRequiredService<IHostEnvironment>();
        if (env.IsDevelopment())
        {
            return exception.Message;
        }

        if (exception is AppException appException)
        {
            return appException.PublicMessage;
        }

        return null;
    }

    private static void LogProblemDetails(int statusCode, Exception exception, HttpContext context,
        ILogger<GlobalExceptionHandler> logger)
    {
        switch (statusCode)
        {
            case >= 400 and < 500:
                logger.LogWarning(exception, "Client error {statusCode} for {Method} {Path}. TraceID: {TraceID}", statusCode,
                    context.Request.Method, context.Request.Path, context.TraceIdentifier);
                break;
            case >= 500 and < 600:
                logger.LogError(exception, "Server error {statusCode} for {Method} {Path}. TraceID: {TraceID}", statusCode,
                    context.Request.Method, context.Request.Path, context.TraceIdentifier);
                break;
        }
    }
}