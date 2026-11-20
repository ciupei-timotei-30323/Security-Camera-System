using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Services;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/events")]
[ServiceFilter(typeof(PiApiKeyAuthFilter))]
public sealed class EventsController(IEventIngestionService eventIngestionService) : ControllerBase
{
    /// <summary>
    /// Ingests a motion frame from the Raspberry Pi. Returns 201 even when the frame
    /// contained no person, so the Pi can keep its burst loop going without retries.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateEvent(
        [FromForm] IFormFile? image,
        [FromForm] DateTimeOffset? capturedAt = null,
        [FromForm] bool isOfflineSync = false,
        CancellationToken ct = default)
    {
        var result = await eventIngestionService.IngestAsync(image, capturedAt, isOfflineSync, ct);

        var message = result.PersonDetected
            ? "Detection event recorded."
            : "Frame processed; no person detected.";

        return StatusCode(StatusCodes.Status201Created, new { message });
    }
}
