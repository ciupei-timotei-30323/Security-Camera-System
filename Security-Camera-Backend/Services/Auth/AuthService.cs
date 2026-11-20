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
}
