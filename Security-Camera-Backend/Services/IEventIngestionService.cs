using Microsoft.AspNetCore.Http;

namespace Security_Camera_Backend.Services;

public interface IEventIngestionService
{
    /// <summary>
    /// Validates, stores and analyses a single motion frame and persists a detection event when a person is found.
    /// </summary>
    /// <param name="image">Uploaded multipart frame.</param>
    /// <param name="capturedAt">Original capture time; only applied when <paramref name="isOfflineSync"/> is true.</param>
    /// <param name="isOfflineSync">True when the Pi is flushing its offline queue.</param>
    /// <param name="systemId">Camera system resolved from the Pi API key.</param>
    /// <param name="clientEventId">Optional idempotency key, unique per system.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EventIngestionResult> IngestAsync(
        IFormFile? image,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        string systemId,
        Guid? clientEventId,
        CancellationToken ct);
}
