using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Auth;

/// <summary>
/// Applies pending EF Core migrations and provisions the built-in <c>admin</c> user from
/// <c>Admin:DeviceCode</c> when it does not exist yet.
/// </summary>
public sealed class AdminUserSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminOptions> adminOptions,
    ILogger<AdminUserSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

        logger.LogInformation("Applying pending database migrations...");
        await dbContext.Database.MigrateAsync(cancellationToken);

        var adminExists = await dbContext.Users
            .AnyAsync(u => u.Username == AdminOptions.AdminUsername, cancellationToken);

        if (adminExists)
        {
            logger.LogInformation("Built-in admin user already exists; skipping seeding.");
            return;
        }

        var deviceCode = adminOptions.Value.DeviceCode;

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            throw new InvalidOperationException(
                "Admin:DeviceCode is not configured and no built-in admin user exists yet. " +
                "Set the Admin__DeviceCode environment variable to the camera's physical device code.");
        }

        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var admin = new User
        {
            Username = AdminOptions.AdminUsername,
            Role = UserRoles.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };

        admin.PasswordHash = passwordHasher.HashPassword(admin, deviceCode);

        dbContext.Users.Add(admin);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded built-in admin user {Username}.", admin.Username);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
