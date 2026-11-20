using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Auth;

public sealed class AuthService(
    SecurityCameraBackendContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IJwtTokenService jwtTokenService,
    ITokenRevocationService tokenRevocationService,
    ILogger<AuthService> logger) : IAuthService
{
    private const string InvalidCredentialsMessage = "Invalid username or password.";

    public async Task<LoginResponse> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        var normalizedUsername = username?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedUsername))
        {
            throw new ValidationAppException(
                "Username is required.",
                "Login request without a username.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationAppException(
                "Password is required.",
                $"Login request for '{normalizedUsername}' without a password.");
        }

        var user = await dbContext.Users
            .SingleOrDefaultAsync(u => u.Username.ToLower() == normalizedUsername.ToLower(), ct);

        if (user is null)
        {
            logger.LogWarning("Login failed for unknown username {Username}.", normalizedUsername);
            throw new UnauthorizedAppException(InvalidCredentialsMessage, InvalidCredentialsMessage);
        }

        var verificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (verificationResult == PasswordVerificationResult.Failed)
        {
            logger.LogWarning("Login failed for user {Username}: invalid password.", user.Username);
            throw new UnauthorizedAppException(InvalidCredentialsMessage, InvalidCredentialsMessage);
        }

        if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Rehashed password for user {Username}.", user.Username);
        }

        var token = jwtTokenService.CreateToken(user);

        logger.LogInformation("User {Username} ({Role}) authenticated.", user.Username, user.Role);

        return new LoginResponse(
            token.Token,
            token.ExpiresAtUtc,
            new LoginUserResponse(user.Id, user.Username, user.Role));
    }

    public async Task LogoutAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);

        if (string.IsNullOrWhiteSpace(jti))
        {
            throw new ValidationAppException(
                "Token is missing the 'jti' claim.",
                "Logout request with a token that has no jti claim.");
        }

        var expirationClaim = principal.FindFirstValue(JwtRegisteredClaimNames.Exp);

        if (!long.TryParse(expirationClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtUnix))
        {
            throw new ValidationAppException(
                "Token is missing a valid 'exp' claim.",
                $"Logout request with an unusable exp claim ('{expirationClaim}') for token {jti}.");
        }

        var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix).UtcDateTime;

        await tokenRevocationService.RevokeAsync(jti, expiresAtUtc, ct);
    }

    public async Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = GetUserId(principal);

        if (userId is null)
        {
            throw new UnauthorizedAppException(
                "Token does not identify a user.",
                "Authenticated principal without a parseable NameIdentifier/Sub claim.");
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId.Value, ct);

        if (user is null)
        {
            logger.LogWarning("Rejected profile request for deleted user {UserId}.", userId.Value);
            throw new UnauthorizedAppException(
                "User account no longer exists.",
                $"Authenticated token references missing user {userId.Value}.");
        }

        return new UserProfileResponse(user.Id, user.Username, user.Role, user.CreatedAtUtc);
    }

    private static int? GetUserId(ClaimsPrincipal principal)
    {
        var rawUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

        return int.TryParse(rawUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }
}
