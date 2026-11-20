using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Security_Camera_Backend.Services.Cv;

public sealed class CvServiceClient(HttpClient httpClient, ILogger<CvServiceClient> logger) : ICvServiceClient
{
    private const int MaxErrorBodyLength = 512;

    public async Task<CvAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var request = new AnalyzeImageRequest(imagePath);

        logger.LogDebug("Requesting CV analysis for {ImagePath}", imagePath);

        using var response = await httpClient.PostAsJsonAsync("/analyze", request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Include the status details (and a bounded slice of the body) so the
            // Global Exception Handler can log a useful diagnostic.
            var body = await ReadErrorBodyAsync(response.Content, ct);
            var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                ? response.StatusCode.ToString()
                : $"{response.StatusCode} ({(int)response.StatusCode}) - {response.ReasonPhrase}";

            logger.LogError("CV service returned {Reason} for {ImagePath}. Body: {Body}",
                reason, imagePath, body);

            throw new HttpRequestException(
                $"CV service failed for '{imagePath}': {reason}. Body: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<CvAnalysisResult>(ct);

        return result ?? throw new HttpRequestException(
            $"CV service returned an empty analysis body for '{imagePath}'.",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static async Task<string> ReadErrorBodyAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            var body = await content.ReadAsStringAsync(ct);

            return body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure to read the error body must not mask the original HTTP failure.
            return $"<unreadable response body: {ex.GetType().Name}>";
        }
    }

    private sealed record AnalyzeImageRequest(
        [property: JsonPropertyName("image_path")] string ImagePath);
}
