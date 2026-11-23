using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Models;
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
    [ProducesResponseType(typeof(PagedEventsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedEventsResponse>> GetPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool suspectOnly = false,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var events = await eventQueryService.GetPagedAsync(
            User.GetRequiredSystemId(),
            new EventQuery(page, pageSize, suspectOnly, from, to),
            ct);

        return Ok(events);
    }

    /// <summary>Returns a single entrance log that belongs to the caller's system.</summary>
    [HttpGet("{id:int}")]
    [Authorize]
    [ProducesResponseType(typeof(DetectionEventResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DetectionEventResponse>> GetById(int id, CancellationToken ct)
    {
        var detectionEvent = await eventQueryService.GetByIdAsync(
            User.GetRequiredSystemId(),
            id,
            ct);

        return detectionEvent is null ? NotFound() : Ok(detectionEvent);
    }

    /// <summary>Deletes an entrance log and its image files. Admin only; cross-tenant ids are 404.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = UserRoles.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await eventQueryService.DeleteAsync(User.GetRequiredSystemId(), id, ct);

        return deleted ? NoContent() : NotFound();
    }

    /// <summary>Serves the face crop of an event. Tenant-scoped and never publicly cacheable.</summary>
    [HttpGet("{id:int}/face")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetFace(int id, CancellationToken ct) =>
        ServeImageAsync(id, face: true, ct);

    /// <summary>
    /// Serves the annotated frame of an event, falling back to the raw capture when the CV
    /// service did not produce an annotated copy. Tenant-scoped and never publicly cacheable.
    /// </summary>
    [HttpGet("{id:int}/annotated")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetAnnotated(int id, CancellationToken ct) =>
        ServeImageAsync(id, face: false, ct);

    /// <summary>
    /// Ingests a motion frame from the Raspberry Pi. Returns 201 even when the frame
    /// contained no person, so the Pi can keep its burst loop going without retries. A retry
    /// with an existing <c>clientEventId</c> returns 200 and the existing event.
    /// </summary>
    [HttpPost]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RequestLimits.MaxEventUploadBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateEvent(
        [FromForm] IFormFile? image,
        [FromForm] DateTimeOffset? capturedAt = null,
        [FromForm] bool isOfflineSync = false,
        [FromForm] Guid? clientEventId = null,
        CancellationToken ct = default)
    {
        var result = await eventIngestionService.IngestAsync(
            image,
            capturedAt,
            isOfflineSync,
            HttpContext.GetResolvedPiSystemId(),
            clientEventId,
            ct);

        if (result.IsDuplicate && result.ProcessingResult?.DetectionEvent is not null)
        {
            return Ok(new
            {
                message = "Existing detection event returned.",
                @event = DetectionEventResponse.FromEntity(result.ProcessingResult.DetectionEvent)
            });
        }

        if (result.ProcessingResult?.DetectionEvent is not null)
        {
            return StatusCode(StatusCodes.Status201Created, new
            {
                message = "Detection event recorded.",
                @event = DetectionEventResponse.FromEntity(result.ProcessingResult.DetectionEvent)
            });
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new { message = "Frame processed; no person detected." });
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
            ct,
            request.ClientEventId);

        return ToAnalyzeResponse(result);
    }

    /// <summary>
    /// Runs the CV + persistence pipeline for an uploaded frame (saved to the
    /// captures volume first).
    /// </summary>
    [HttpPost("analyze")]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RequestLimits.MaxEventUploadBytes)]
    [ProducesResponseType(typeof(DetectionEventResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AnalyzeUpload(
        [FromForm] IFormFile? image,
        [FromForm] DateTimeOffset? capturedAt = null,
        [FromForm] bool isOfflineSync = false,
        [FromForm] Guid? clientEventId = null,
        CancellationToken ct = default)
    {
        var rawImagePath = await captureStorage.SaveFrameAsync(image, ct);

        var result = await detectionProcessingService.ProcessCapturedImageAsync(
            rawImagePath,
            capturedAt,
            isOfflineSync,
            HttpContext.GetResolvedPiSystemId(),
            ct,
            clientEventId);

        return ToAnalyzeResponse(result);
    }

    private async Task<IActionResult> ServeImageAsync(int id, bool face, CancellationToken ct)
    {
        var systemId = User.GetRequiredSystemId();
        var detectionEvent = await eventQueryService.GetEntityAsync(systemId, id, ct);

        if (detectionEvent is null)
        {
            return NotFound();
        }

        var storedPath = face
            ? detectionEvent.DetectedFaceUri
            : detectionEvent.AnnotatedImageUri ?? detectionEvent.FullImageUri;

        var fullPath = captureStorage.TryResolveExistingCapturePath(storedPath);

        if (fullPath is null)
        {
            return NotFound();
        }

        Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";

        return PhysicalFile(fullPath, GetContentType(fullPath));
    }

    private IActionResult ToAnalyzeResponse(DetectionProcessingResult result)
    {
        if (!result.Persisted || result.DetectionEvent is null)
        {
            return Ok(new { persisted = false, message = "No person or face detected." });
        }

        var response = DetectionEventResponse.FromEntity(result.DetectionEvent);

        return result.IsDuplicate
            ? Ok(response)
            : StatusCode(StatusCodes.Status201Created, response);
    }

    private static string GetContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
}
