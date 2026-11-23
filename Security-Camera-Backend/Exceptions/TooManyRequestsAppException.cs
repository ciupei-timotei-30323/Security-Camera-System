using System.Net;

namespace Security_Camera_Backend.Exceptions;

/// <summary>
/// Too many requests (login rate limit or temporary lockout). Handled by the existing
/// <see cref="GlobalExceptionHandler"/> as <c>429 Too Many Requests</c>.
/// </summary>
public sealed class TooManyRequestsAppException : AppException
{
    /// <summary>Value for the <c>Retry-After</c> header, when the caller can say how long to wait.</summary>
    public TimeSpan? RetryAfter { get; }

    public TooManyRequestsAppException(
        string publicMessage,
        string internalMessage,
        TimeSpan? retryAfter = null,
        string? errorCode = null)
        : base(publicMessage, internalMessage, HttpStatusCode.TooManyRequests, errorCode)
    {
        RetryAfter = retryAfter;
    }
}
