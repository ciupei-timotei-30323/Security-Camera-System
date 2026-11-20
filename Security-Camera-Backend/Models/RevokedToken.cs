namespace Security_Camera_Backend.Models;

/// <summary>
/// A server-side deny-list entry for a JWT that was explicitly logged out.
/// Rows are pruned once the token would have expired anyway.
/// </summary>
public class RevokedToken
{
    public int Id { get; set; }

    public string Jti { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime RevokedAtUtc { get; set; }
}
