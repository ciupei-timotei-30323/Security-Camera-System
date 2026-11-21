using Microsoft.AspNetCore.Http;

namespace Security_Camera_Backend.Services;

public interface IEventIngestionService
{
    /// <summary>
    /// Validates, stores and analyses a single motion frame and persists a detection event when a person is found.
    /// </summary>
    /// <param name="image">Uploaded multipart frame.</param>
    /// <param name="capturedAt">Original capture time for offline-queued frames; defaults to now (UTC).</param>
    /// <param name="isOfflineSync">True when the Pi is flushing its offline queue.</param>
    /// <param name="systemId">Camera system resolved from the Pi API key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EventIngestionResult> IngestAsync(
        IFormFile? image,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        string systemId,
        CancellationToken ct);
}
