using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Users;

public sealed class UserManagementService(
    SecurityCameraBackendContext dbContext,
    IPasswordHasher<User> passwordHasher,
    ILogger<UserManagementService> logger) : IUserManagementService
{
    public async Task<UserSummaryResponse> CreateAsync(
        string systemId,
        string adminUsername,
        CreateUserRequest request,
        CancellationToken ct)
    {
        var username = request.Username?.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ValidationAppException(
                "Username is required.",
                "Create-user request without a username.");
        }

        if (username.Length > 64)
        {
            throw new ValidationAppException(
                "Username must be at most 64 characters.",
                $"Create-user request username length {username.Length} exceeds 64.");
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ValidationAppException(
                "Password is required.",
                $"Create-user request for '{username}' without a password.");
        }

        var role = string.IsNullOrWhiteSpace(request.Role) ? UserRoles.User : request.Role.Trim();

        if (role is not (UserRoles.Admin or UserRoles.User))
        {
            throw new ValidationAppException(
                $"Role must be '{UserRoles.Admin}' or '{UserRoles.User}'.",
                $"Create-user request for '{username}' used unsupported role '{role}'.");
        }

        if (string.Equals(username, adminUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "Username is already in use.",
                $"Admin '{adminUsername}' attempted to create a sub-user with their own username.");
        }

        var lowerUsername = username.ToLowerInvariant();

        var duplicateExists = await dbContext.Users
            .AnyAsync(u => u.SystemId == systemId && u.Username.ToLower() == lowerUsername, ct);

        if (duplicateExists)
        {
            throw new ValidationAppException(
                "Username is already in use in this camera system.",
                $"User '{username}' already exists in system {systemId}.");
        }

        var user = new User
        {
            SystemId = systemId,
            Username = username,
            Role = role,
            CreatedAtUtc = DateTime.UtcNow
        };

        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The composite (SystemId, Username) index is the final arbiter under concurrency.
            dbContext.Entry(user).State = EntityState.Detached;

            logger.LogWarning(ex,
                "Rejected duplicate sub-user {Username} for system {SystemId}.",
                username, systemId);

            throw new ValidationAppException(
                "Username is already in use in this camera system.",
                $"Unique index violation while creating '{username}' in system {systemId}.");
        }

        logger.LogInformation(
            "Admin {AdminUsername} created sub-user {Username} ({Role}) in system {SystemId}.",
            adminUsername, user.Username, user.Role, systemId);

        return Map(user);
    }

    public async Task<IReadOnlyList<UserSummaryResponse>> GetAllAsync(string systemId, CancellationToken ct)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.SystemId == systemId)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        return users.Select(Map).ToList();
    }

    public async Task<UserSummaryResponse?> GetByIdAsync(string systemId, int userId, CancellationToken ct)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId && u.SystemId == systemId, ct);

        return user is null ? null : Map(user);
    }

    public async Task<bool> DeleteAsync(
        string systemId,
        int? requesterUserId,
        string requesterUsername,
        int userId,
        CancellationToken ct)
    {
        var user = await dbContext.Users
            .SingleOrDefaultAsync(u => u.Id == userId && u.SystemId == systemId, ct);

        if (user is null)
        {
            return false;
        }

        if (requesterUserId == user.Id ||
            string.Equals(requesterUsername, user.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "You cannot delete your own account.",
                $"Admin {requesterUsername} attempted to delete their own user {userId}.");
        }

        if (string.Equals(user.Username, AdminOptions.AdminUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "The factory admin account cannot be deleted.",
                $"Attempted to delete built-in factory admin {userId}.");
        }

        dbContext.Users.Remove(user);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation(
            "Admin {AdminUsername} deleted sub-user {Username} ({UserId}) in system {SystemId}.",
            requesterUsername, user.Username, user.Id, systemId);

        return true;
    }

    private static UserSummaryResponse Map(User user) =>
        new(user.Id, user.SystemId, user.Username, user.Role, user.CreatedAtUtc);
}
