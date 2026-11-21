using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Services;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemStatusController(ISystemStatusService systemStatusService) : ControllerBase
{
    /// <summary>Returns camera/terminal presence and backend/cv-service health for the tenant.</summary>
    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(typeof(SystemStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SystemStatusResponse>> GetStatus(CancellationToken ct) =>
        Ok(await systemStatusService.GetAsync(User.GetRequiredSystemId(), ct));
}
