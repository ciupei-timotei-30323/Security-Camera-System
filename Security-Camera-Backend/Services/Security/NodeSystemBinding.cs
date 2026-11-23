using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Services.Security;

/// <summary>
/// Enforces that a token belongs to <em>this</em> Pi/backend.
///
/// A single-Pi deployment must not accept a token minted for another camera system even when the
/// signature verifies (shared key reuse or a leaked signing key). Set <c>PiAuth:NodeSystemId</c> to
/// enable the check; leaving it empty keeps multi-system backends working.
/// </summary>
public static class NodeSystemBinding
{
    /// <summary>
    /// True when the token may be used on this node. Always true when
    /// <see cref="PiAuthOptions.NodeSystemId"/> is not configured.
    /// </summary>
    public static bool IsTokenBoundToNode(string? tokenSystemId, PiAuthOptions piAuth)
    {
        var nodeSystemId = piAuth.NodeSystemId?.Trim();

        if (string.IsNullOrWhiteSpace(nodeSystemId))
        {
            return true;
        }

        return string.Equals(tokenSystemId, nodeSystemId, StringComparison.OrdinalIgnoreCase);
    }
}
