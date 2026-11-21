using System.Net;

namespace Security_Camera_Backend.Exceptions;

public abstract class AppException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string PublicMessage { get; }

    /// <summary>Optional machine-readable code surfaced as the ProblemDetails <c>code</c> extension.</summary>
    public string? ErrorCode { get; }

    protected AppException(
        string publicMessage,
        string internalMessage,
        HttpStatusCode statusCode = HttpStatusCode.InternalServerError,
        string? errorCode = null) :
        base(internalMessage)
    {
        StatusCode = statusCode;
        PublicMessage = publicMessage;
        ErrorCode = errorCode;
    }
    
}