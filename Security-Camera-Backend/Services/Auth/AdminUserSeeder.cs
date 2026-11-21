using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Systems;

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

        // Older deployments only had a single Pi. Keep that deployment working as sys-001 and
        // register its configured API key against the default system.
        var systemRegistry = scope.ServiceProvider.GetRequiredService<ISystemRegistry>();
        var piAuth = scope.ServiceProvider
            .GetRequiredService<IOptions<PiAuthOptions>>()
            .Value;

        await systemRegistry.EnsureSystemExistsAsync(
            CameraSystem.DefaultSystemId,
            "Default camera system",
            piAuth.ApiKey,
            cancellationToken);

        // Register every configured key as a real device so device tracking and system status
        // work for both legacy and multi-tenant deployments.
        foreach (var (systemId, apiKey) in piAuth.Systems)
        {
            if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            await systemRegistry.EnsureSystemExistsAsync(
                systemId,
                $"Camera system {systemId}",
                apiKey,
                cancellationToken);
        }

        // Alert terminals use the same provisioning path but are marked as AlertTerminal so
        // camera keys cannot poll the terminal endpoint and terminals cannot ingest frames.
        foreach (var (systemId, apiKey) in piAuth.AlertTerminalSystems)
        {
            if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            await systemRegistry.EnsureSystemExistsAsync(
                systemId,
                $"Camera system {systemId}",
                apiKey,
                cancellationToken,
                DeviceType.AlertTerminal);
        }

        var adminExists = await dbContext.Users
            .AnyAsync(
                u => u.Username == AdminOptions.AdminUsername &&
                     u.SystemId == CameraSystem.DefaultSystemId,
                cancellationToken);

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
            SystemId = CameraSystem.DefaultSystemId,
            Username = AdminOptions.AdminUsername,
            Role = UserRoles.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };

        admin.PasswordHash = passwordHasher.HashPassword(admin, deviceCode);

        dbContext.Users.Add(admin);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded built-in admin user {Username} for system {SystemId}.",
            admin.Username, admin.SystemId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
