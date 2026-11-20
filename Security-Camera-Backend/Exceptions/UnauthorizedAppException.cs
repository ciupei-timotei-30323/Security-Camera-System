using System.Net;

namespace Security_Camera_Backend.Exceptions;

/// <summary>
/// Failed authentication (e.g. invalid credentials). Handled by the existing
/// <see cref="GlobalExceptionHandler"/> as <c>401 Unauthorized</c>.
/// </summary>
public sealed class UnauthorizedAppException : AppException
{
    public UnauthorizedAppException(string publicMessage, string internalMessage)
        : base(publicMessage, internalMessage, HttpStatusCode.Unauthorized)
    {
    }
}
