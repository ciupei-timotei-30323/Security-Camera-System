using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/device")]
public sealed class DeviceController(
    SecurityCameraBackendContext dbContext,
    ISystemStateService systemStateService,
    IAlarmStateStore alarmStateStore,
    TimeProvider timeProvider) : ControllerBase
{
    private const int DisplayWidth = 16;

    /// <summary>
    /// Polled by an alert terminal. The API key must belong to an <see cref="DeviceType.AlertTerminal"/>
    /// device; camera keys are rejected.
    /// </summary>
    [HttpGet("status")]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
    [ProducesResponseType(typeof(DeviceTerminalStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeviceTerminalStatusResponse>> GetStatus(CancellationToken ct)
    {
        var providedKey = Request.Headers[PiApiKeyAuthFilter.HeaderName].ToString();
        var apiKeyHash = PiApiKeyHasher.HashToHex(providedKey);

        var isAlertTerminal = await dbContext.PiDevices
            .AsNoTracking()
            .AnyAsync(device => device.ApiKeyHash == apiKeyHash &&
                                device.DeviceType == DeviceType.AlertTerminal,
                ct);

        if (!isAlertTerminal)
        {
            return Unauthorized(new { message = "A valid alert-terminal API key is required." });
        }

        var systemId = HttpContext.GetResolvedPiSystemId();
        var now = timeProvider.GetUtcNow();
        var armed = await systemStateService.IsArmedAsync(systemId, now, ct);
        var alarmUntil = alarmStateStore.GetActiveUntil(systemId);
        var alarm = alarmUntil is not null;

        var line1 = alarm ? "SUSPECT DETECTED" : armed ? "ARMED" : "DISARMED";

        Response.Headers[HeaderNames.CacheControl] = "no-store";

        return Ok(new DeviceTerminalStatusResponse(
            armed,
            alarm,
            alarmUntil,
            ToDisplayLine(line1),
            ToDisplayLine(string.Empty),
            now));
    }

    private static string ToDisplayLine(string value) =>
        value.Length >= DisplayWidth
            ? value[..DisplayWidth]
            : value.PadRight(DisplayWidth);
}
