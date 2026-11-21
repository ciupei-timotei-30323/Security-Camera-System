using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Services;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController(
    IEventIngestionService eventIngestionService,
    IDetectionProcessingService detectionProcessingService,
    ICaptureStorage captureStorage,
    IEventQueryService eventQueryService) : ControllerBase
{
    /// <summary>
    /// Lists entrance logs for the authenticated user's camera system, newest first.
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<DetectionEventResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<DetectionEventResponse>>> GetRecent(
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var events = await eventQueryService.GetRecentAsync(
            User.GetRequiredSystemId(),
            limit,
            ct);

        return Ok(events);
    }

    /// <summary>
    /// Ingests a motion frame from the Raspberry Pi. Returns 201 even when the frame
    /// contained no person, so the Pi can keep its burst loop going without retries.
    /// </summary>
    [HttpPost]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
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
        var result = await eventIngestionService.IngestAsync(
            image,
            capturedAt,
            isOfflineSync,
            HttpContext.GetResolvedPiSystemId(),
            ct);

        var message = result.PersonDetected
            ? "Detection event recorded."
            : "Frame processed; no person detected.";

        return StatusCode(StatusCodes.Status201Created, new { message });
    }

    /// <summary>
    /// Runs the CV + persistence pipeline for a frame that already exists on the
    /// captures volume. Useful for internal callers and manual testing.
    /// </summary>
    [HttpPost("analyze")]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(DetectionEventResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AnalyzeByPath(
        [FromBody] AnalyzeImageRequest request,
        CancellationToken ct)
    {
        var rawImagePath = captureStorage.ResolveExistingCapturePath(request.ImagePath);

        var result = await detectionProcessingService.ProcessCapturedImageAsync(
            rawImagePath,
            request.CapturedAt,
            request.IsOfflineSync,
            HttpContext.GetResolvedPiSystemId(),
            ct);

        return ToAnalyzeResponse(result);
    }

    /// <summary>
    /// Runs the CV + persistence pipeline for an uploaded frame (saved to the
    /// captures volume first).
    /// </summary>
    [HttpPost("analyze")]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(DetectionEventResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AnalyzeUpload(
        [FromForm] IFormFile? image,
        [FromForm] DateTimeOffset? capturedAt = null,
        [FromForm] bool isOfflineSync = false,
        CancellationToken ct = default)
    {
        var rawImagePath = await captureStorage.SaveFrameAsync(image, ct);

        var result = await detectionProcessingService.ProcessCapturedImageAsync(
            rawImagePath,
            capturedAt,
            isOfflineSync,
            HttpContext.GetResolvedPiSystemId(),
            ct);

        return ToAnalyzeResponse(result);
    }

    private IActionResult ToAnalyzeResponse(DetectionProcessingResult result)
    {
        if (!result.Persisted || result.DetectionEvent is null)
        {
            return Ok(new { persisted = false, message = "No person or face detected." });
        }

        return StatusCode(
            StatusCodes.Status201Created,
            DetectionEventResponse.FromEntity(result.DetectionEvent));
    }
}
