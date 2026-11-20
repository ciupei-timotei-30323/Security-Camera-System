using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Auth;

public sealed class TokenRevocationService(
    SecurityCameraBackendContext dbContext,
    ILogger<TokenRevocationService> logger) : ITokenRevocationService
{
    public Task<bool> IsRevokedAsync(string jti, CancellationToken ct) =>
        dbContext.RevokedTokens
            .AsNoTracking()
            .AnyAsync(token => token.Jti == jti, ct);

    public async Task RevokeAsync(string jti, DateTime expiresAtUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Expired entries can never match a valid token again, so drop them opportunistically
        // instead of relying on a background job.
        var pruned = await dbContext.RevokedTokens
            .Where(token => token.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync(ct);

        if (pruned > 0)
        {
            logger.LogInformation("Pruned {Count} expired revoked token(s).", pruned);
        }

        var alreadyRevoked = await dbContext.RevokedTokens
            .AnyAsync(token => token.Jti == jti, ct);

        if (alreadyRevoked)
        {
            logger.LogInformation("Token {Jti} is already revoked.", jti);
            return;
        }

        dbContext.RevokedTokens.Add(new RevokedToken
        {
            Jti = jti,
            ExpiresAtUtc = expiresAtUtc,
            RevokedAtUtc = now
        });

        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Revoked token {Jti} until {ExpiresAtUtc:o}.", jti, expiresAtUtc);
    }
}
