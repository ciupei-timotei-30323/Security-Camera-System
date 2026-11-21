using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Filters;

namespace Security_Camera_Backend.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// Returns the system resolved by <see cref="PiApiKeyAuthFilter"/> for this request.
    /// Only valid on actions guarded by that filter.
    /// </summary>
    public static string GetResolvedPiSystemId(this HttpContext context)
    {
        if (context.Items.TryGetValue(PiApiKeyAuthFilter.SystemIdItemKey, out var value) &&
            value is string systemId &&
            !string.IsNullOrWhiteSpace(systemId))
        {
            return systemId;
        }

        throw new UnauthorizedAppException(
            "Camera system could not be resolved from the API key.",
            "Pi API key filter did not place a SystemId in HttpContext.Items.");
    }
}
