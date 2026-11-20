using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Services;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/events")]
[ServiceFilter(typeof(PiApiKeyAuthFilter))]
public sealed class EventsController(
    IEventIngestionService eventIngestionService,
    IDetectionProcessingService detectionProcessingService,
    ICaptureStorage captureStorage) : ControllerBase
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

    /// <summary>
    /// Runs the CV + Neon persistence pipeline for a frame that already exists on the
    /// captures volume. Useful for internal callers and manual testing.
    /// </summary>
    [HttpPost("analyze")]
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
            rawImagePath, request.CapturedAt, request.IsOfflineSync, ct);

        return ToAnalyzeResponse(result);
    }

    /// <summary>
    /// Runs the CV + Neon persistence pipeline for an uploaded frame (saved to the
    /// captures volume first).
    /// </summary>
    [HttpPost("analyze")]
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
            rawImagePath, capturedAt, isOfflineSync, ct);

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
