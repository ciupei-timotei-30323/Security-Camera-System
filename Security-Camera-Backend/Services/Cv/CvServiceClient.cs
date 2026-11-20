using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Security_Camera_Backend.Services.Cv;

public sealed class CvServiceClient(HttpClient httpClient, ILogger<CvServiceClient> logger) : ICvServiceClient
{
    public async Task<CvAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var request = new AnalyzeImageRequest(imagePath);

        logger.LogDebug("Requesting CV analysis for {ImagePath}", imagePath);

        using var response = await httpClient.PostAsJsonAsync("/analyze", request, ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("CV service returned {StatusCode} for {ImagePath}", (int)response.StatusCode, imagePath);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<CvAnalysisResult>(ct);

        return result ?? throw new InvalidOperationException(
            $"CV service returned an empty analysis body for '{imagePath}'.");
    }

    private sealed record AnalyzeImageRequest(
        [property: JsonPropertyName("image_path")] string ImagePath);
}
