namespace Security_Camera_Backend.Services.Cv;

public interface ICvServiceClient
{
    /// <summary>
    /// Asks the internal vision service to analyse an image that is already saved on the shared volume.
    /// </summary>
    /// <param name="imagePath">Full path to the image on the shared volume.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CvAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken ct);

    /// <summary>Checks the internal cv-service <c>/health</c> endpoint.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);
}
