using System.Net;

namespace Security_Camera_Backend.Exceptions;

/// <summary>
/// A client-facing validation failure. Handled by the existing <see cref="GlobalExceptionHandler"/>
/// as <c>400 Bad Request</c>.
/// </summary>
public sealed class ValidationAppException : AppException
{
    public ValidationAppException(string publicMessage, string internalMessage)
        : base(publicMessage, internalMessage, HttpStatusCode.BadRequest)
    {
    }
}
