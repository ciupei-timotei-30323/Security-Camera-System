using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.ExternalAuth;
using Security_Camera_Backend.Services.Systems;

namespace Security_Camera_Backend.Services.Auth;

public sealed class AuthService(
    SecurityCameraBackendContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IJwtTokenService jwtTokenService,
    ITokenRevocationService tokenRevocationService,
    IExternalAdminAuthClient externalAdminAuthClient,
    ISystemRegistry systemRegistry,
    LoginLockoutTracker loginLockoutTracker,
    ILogger<AuthService> logger) : IAuthService
{
    public const string SystemIdRequiredCode = "SYSTEM_ID_REQUIRED";

    public const string LoginLockedCode = "LOGIN_LOCKED";

    private const string InvalidCredentialsMessage = "Invalid username or password.";

    public async Task<LoginResponse> LoginAsync(
        string? username,
        string? password,
        string? systemId,
        CancellationToken ct)
    {
        var normalizedUsername = username?.Trim();
        var normalizedSystemId = string.IsNullOrWhiteSpace(systemId) ? null : systemId.Trim();

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

        if (normalizedSystemId is { Length: > 64 })
        {
            throw new ValidationAppException(
                "SystemId must be at most 64 characters.",
                $"Login request supplied a {normalizedSystemId.Length}-character systemId.");
        }

        // Temporary lockout: a locked identity cannot even try, which caps online guessing at
        // MaxFailures per LockoutSeconds per username/system pair.
        if (loginLockoutTracker.IsLockedOut(normalizedUsername, normalizedSystemId, out var retryAfter))
        {
            logger.LogWarning(
                "Rejected login for {Username} (system: {SystemId}): locked out for another {RetryAfterSeconds}s.",
                normalizedUsername, normalizedSystemId ?? "<any>", (int)retryAfter.TotalSeconds);

            throw new TooManyRequestsAppException(
                "Too many failed login attempts. Try again later.",
                $"Username '{normalizedUsername}' is locked out for another {retryAfter}.",
                retryAfter,
                LoginLockedCode);
        }

        // Step A: factory administrators are validated by the external IAM service.
        var externalAdmin = await externalAdminAuthClient.ValidateAdminAsync(
            normalizedUsername, password, ct);

        if (externalAdmin is not null)
        {
            if (normalizedSystemId is not null &&
                !string.Equals(normalizedSystemId, externalAdmin.SystemId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "External administrator {Username} belongs to {ActualSystemId}, not requested {RequestedSystemId}.",
                    externalAdmin.Username, externalAdmin.SystemId, normalizedSystemId);

                throw new UnauthorizedAppException(InvalidCredentialsMessage, InvalidCredentialsMessage);
            }

            await systemRegistry.EnsureSystemExistsAsync(
                externalAdmin.SystemId,
                externalAdmin.Username,
                externalAdmin.PiApiKey,
                ct);

            var adminToken = jwtTokenService.CreateToken(
                subject: externalAdmin.Username,
                username: externalAdmin.Username,
                role: UserRoles.Admin,
                systemId: externalAdmin.SystemId,
                externalAdmin: true);

            loginLockoutTracker.Reset(normalizedUsername, normalizedSystemId);

            logger.LogInformation(
                "External administrator {Username} authenticated for system {SystemId}.",
                externalAdmin.Username, externalAdmin.SystemId);

            return new LoginResponse(
                adminToken.Token,
                adminToken.ExpiresAtUtc,
                new LoginUserResponse(
                    null,
                    externalAdmin.Username,
                    UserRoles.Admin,
                    externalAdmin.SystemId));
        }

        // Step B: fall back to a locally stored sub-user (or the built-in admin).
        var matchingUsers = await FindLocalUsersAsync(normalizedUsername, normalizedSystemId, ct);

        // PASSWORD FIRST: the password is checked against every candidate record before any
        // ambiguity is reported. Answering SYSTEM_ID_REQUIRED for a username alone would turn this
        // endpoint into a username oracle, so a wrong password must always look like a plain 401.
        var authenticatedCandidates = new List<(User User, PasswordVerificationResult Result)>();

        foreach (var candidate in matchingUsers)
        {
            var candidateResult = passwordHasher.VerifyHashedPassword(
                candidate, candidate.PasswordHash, password);

            if (candidateResult != PasswordVerificationResult.Failed)
            {
                authenticatedCandidates.Add((candidate, candidateResult));
            }
        }

        if (authenticatedCandidates.Count == 0)
        {
            loginLockoutTracker.RegisterFailure(normalizedUsername, normalizedSystemId);

            logger.LogWarning(
                matchingUsers.Count == 0
                    ? "Login failed for unknown username {Username} (system: {SystemId})."
                    : "Login failed for user {Username} (system: {SystemId}): invalid password.",
                normalizedUsername, normalizedSystemId ?? "<any>");

            throw new UnauthorizedAppException(InvalidCredentialsMessage, InvalidCredentialsMessage);
        }

        if (normalizedSystemId is null && authenticatedCandidates.Count > 1)
        {
            logger.LogWarning(
                "Login for username {Username} matched the password in {Count} systems; SYSTEM_ID_REQUIRED.",
                normalizedUsername, authenticatedCandidates.Count);

            throw new ValidationAppException(
                "Multiple systems have a user with this username; please provide your Camera System ID.",
                $"Username '{normalizedUsername}' with a matching password exists in systems: " +
                string.Join(", ", authenticatedCandidates
                    .Select(candidate => candidate.User.SystemId)
                    .OrderBy(id => id)) + ".",
                SystemIdRequiredCode);
        }

        var (user, verificationResult) = authenticatedCandidates[0];

        loginLockoutTracker.Reset(normalizedUsername, normalizedSystemId);

        if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation(
                "Rehashed password for user {Username} in system {SystemId}.",
                user.Username, user.SystemId);
        }

        var token = jwtTokenService.CreateToken(user);

        logger.LogInformation(
            "Local user {Username} ({Role}) authenticated for system {SystemId}.",
            user.Username, user.Role, user.SystemId);

        return new LoginResponse(
            token.Token,
            token.ExpiresAtUtc,
            new LoginUserResponse(user.Id, user.Username, user.Role, user.SystemId));
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
        if (principal.IsExternalAdmin())
        {
            return new UserProfileResponse(
                Id: null,
                Username: principal.GetRequiredUsername(),
                Role: UserRoles.Admin,
                SystemId: principal.GetRequiredSystemId(),
                CreatedAtUtc: null);
        }

        var userId = GetUserId(principal);

        if (userId is null)
        {
            throw new UnauthorizedAppException(
                "Token does not identify a user.",
                "Authenticated principal without a parseable sub/NameIdentifier claim.");
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

        var tokenSystemId = principal.GetRequiredSystemId();

        if (!string.Equals(tokenSystemId, user.SystemId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rejected profile request for user {UserId}: token system {TokenSystemId} != user system {UserSystemId}.",
                user.Id, tokenSystemId, user.SystemId);

            throw new UnauthorizedAppException(
                "Token does not match this user's camera system.",
                $"Token system_id '{tokenSystemId}' does not match user {user.Id} system '{user.SystemId}'.");
        }

        return new UserProfileResponse(
            user.Id,
            user.Username,
            user.Role,
            user.SystemId,
            user.CreatedAtUtc);
    }

    private async Task<List<User>> FindLocalUsersAsync(
        string username,
        string? systemId,
        CancellationToken ct)
    {
        var query = dbContext.Users.AsQueryable();

        if (systemId is not null)
        {
            query = query.Where(u => u.SystemId == systemId);
        }

        var lowerUsername = username.ToLowerInvariant();

        return await query
            .Where(u => u.Username.ToLower() == lowerUsername)
            .ToListAsync(ct);
    }

    private static int? GetUserId(ClaimsPrincipal principal)
    {
        var rawUserId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(rawUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }
}
