using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Services.Systems;

/// <summary>
/// Keeps the local <c>CameraSystems</c>/<c>PiDevices</c> rows in sync with the external factory
/// service. Idempotent: repeated logins and concurrent logins are safe.
/// </summary>
public interface ISystemRegistry
{
    Task EnsureSystemExistsAsync(
        string systemId,
        string? displayName,
        string? piApiKey,
        CancellationToken ct);
}

public sealed class SystemRegistry(
    SecurityCameraBackendContext dbContext,
    ILogger<SystemRegistry> logger) : ISystemRegistry
{
    public async Task EnsureSystemExistsAsync(
        string systemId,
        string? displayName,
        string? piApiKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);

        var system = await dbContext.CameraSystems
            .SingleOrDefaultAsync(s => s.SystemId == systemId, ct);

        if (system is null)
        {
            system = new CameraSystem
            {
                SystemId = systemId,
                DisplayName = displayName,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.CameraSystems.Add(system);
        }
        else if (string.IsNullOrWhiteSpace(system.DisplayName) && !string.IsNullOrWhiteSpace(displayName))
        {
            system.DisplayName = displayName;
        }

        if (!string.IsNullOrWhiteSpace(piApiKey))
        {
            var apiKeyHash = PiApiKeyHasher.HashToHex(piApiKey);

            var device = await dbContext.PiDevices
                .SingleOrDefaultAsync(d => d.ApiKeyHash == apiKeyHash, ct);

            if (device is null)
            {
                dbContext.PiDevices.Add(new PiDevice
                {
                    SystemId = systemId,
                    ApiKeyHash = apiKeyHash,
                    Name = $"{displayName ?? systemId} camera kit",
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
            else if (!string.Equals(device.SystemId, systemId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Camera kit {ApiKeyHash} moved from system {OldSystemId} to {NewSystemId}.",
                    apiKeyHash, device.SystemId, systemId);
                device.SystemId = systemId;
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent login may have inserted the same system/device first. The rows now
            // exist, which is all this call guarantees.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(ex,
                "Concurrent registration for system {SystemId}; treating the existing rows as authoritative.",
                systemId);
        }
    }
}
