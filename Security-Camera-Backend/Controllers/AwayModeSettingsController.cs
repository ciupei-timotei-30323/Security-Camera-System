using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Services.Mqtt;
using Security_Camera_Backend.Services.Settings;

namespace Security_Camera_Backend.Controllers;

/// <summary>
/// Per-system arm/disarm settings. Both routes are supported so existing dashboards can migrate
/// without a breaking URL change.
/// </summary>
[ApiController]
[Route("api/away-mode-settings")]
[Route("api/settings/away-mode")]
[Authorize]
public sealed class AwayModeSettingsController(
    IAwayModeSettingsService settingsService,
    IRealtimePublisher realtimePublisher) : ControllerBase
{
    /// <summary>
    /// Returns the current setting, creating the row with <c>isArmed = true</c> on first access.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AwayModeSettingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AwayModeSettingResponse>> Get(CancellationToken ct)
    {
        var setting = await settingsService.GetOrCreateAsync(User.GetRequiredSystemId(), ct);

        return Ok(setting);
    }

    [HttpPut]
    [ProducesResponseType(typeof(AwayModeSettingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AwayModeSettingResponse>> Update(
        [FromBody] UpdateAwayModeSettingRequest request,
        CancellationToken ct)
    {
        var systemId = User.GetRequiredSystemId();
        var setting = await settingsService.UpdateAsync(systemId, request, ct);

        // Arming state changed: push the retained alarm document immediately so the ESP32 and any
        // open dashboard reflect it without waiting for the next poll or detection.
        await realtimePublisher.PublishAlarmStateAsync(systemId, ct);

        return Ok(setting);
    }
}
