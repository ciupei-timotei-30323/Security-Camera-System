using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Filters;
using Security_Camera_Backend.Services.Camera;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/camera")]
public sealed class CameraController(
    ILatestFrameStore latestFrameStore,
    IFrameStreamLimiter frameStreamLimiter) : ControllerBase
{
    public const string FrameRateLimitPolicy = "camera-frame";
    public const int MaxFrameBytes = 200 * 1024;

    private const string MultipartBoundary = "frame";
    private static readonly TimeSpan StreamPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Stores the newest JPEG from the Raspberry Pi for live dashboard preview.</summary>
    [HttpPost("frame")]
    [ServiceFilter(typeof(PiApiKeyAuthFilter))]
    [EnableRateLimiting(FrameRateLimitPolicy)]
    [RequestSizeLimit(RequestLimits.MaxFrameUploadBytes)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> UploadFrame(CancellationToken ct)
    {
        if (!IsJpegContentType(Request.ContentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (Request.ContentLength > MaxFrameBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var frame = await TryReadFrameAsync(Request.Body, ct);

        if (frame is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (frame.Length == 0)
        {
            return BadRequest();
        }

        // A declared image/jpeg proves nothing: the newest frame is later served verbatim to every
        // dashboard of the system, so the bytes themselves must be a JPEG.
        if (!ImageSignatureValidator.IsJpeg(frame))
        {
            return BadRequest();
        }

        latestFrameStore.Set(
            HttpContext.GetResolvedPiSystemId(),
            frame,
            DateTimeOffset.UtcNow);

        return StatusCode(StatusCodes.Status202Accepted);
    }

    /// <summary>Returns the newest JPEG for the caller's system, or 204 when absent/stale.</summary>
    [HttpGet("latest")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetLatest()
    {
        if (!latestFrameStore.TryGetLatest(
                User.GetRequiredSystemId(),
                DateTimeOffset.UtcNow,
                out var frame))
        {
            return NoContent();
        }

        Response.Headers[HeaderNames.CacheControl] = "no-store";

        return File(frame.Jpeg, "image/jpeg");
    }

    /// <summary>
    /// Relays the system's latest frame as an MJPEG stream. The connection is held until the
    /// client disconnects; at most five streams per system are allowed.
    /// </summary>
    [HttpGet("stream")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Stream(CancellationToken ct)
    {
        var systemId = User.GetRequiredSystemId();

        if (!frameStreamLimiter.TryAcquire(systemId, out var lease))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        using (lease)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = $"multipart/x-mixed-replace; boundary={MultipartBoundary}";
            Response.Headers[HeaderNames.CacheControl] = "no-store, no-transform";
            Response.Headers["X-Accel-Buffering"] = "no";

            long lastVersion = -1;

            try
            {
                // Send the multipart headers immediately so a dashboard can connect even before
                // the Pi has produced its first frame.
                await Response.StartAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    if (latestFrameStore.TryGetLatest(
                            systemId,
                            DateTimeOffset.UtcNow,
                            out var frame) &&
                        frame.Version != lastVersion)
                    {
                        await WriteFrameAsync(frame.Jpeg, ct);
                        lastVersion = frame.Version;
                    }

                    await Task.Delay(StreamPollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal client disconnect.
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                // The socket can disappear between the cancellation check and the write.
            }

            return new EmptyResult();
        }
    }

    private async Task WriteFrameAsync(byte[] jpeg, CancellationToken ct)
    {
        var header = System.Text.Encoding.ASCII.GetBytes(
            $"--{MultipartBoundary}\r\n" +
            "Content-Type: image/jpeg\r\n" +
            $"Content-Length: {jpeg.Length}\r\n\r\n");

        await Response.Body.WriteAsync(header, ct);
        await Response.Body.WriteAsync(jpeg, ct);
        await Response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
        await Response.Body.FlushAsync(ct);
    }

    private static async Task<byte[]?> TryReadFrameAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await body.ReadAsync(chunk, ct);

            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxFrameBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsJpegContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();

        return string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mediaType, "image/jpg", StringComparison.OrdinalIgnoreCase);
    }
}
