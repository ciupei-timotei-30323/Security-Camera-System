using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Security_Camera_Backend.Services.ExternalAuth;

/// <summary>
/// Calls the future factory IAM service:
/// <c>POST {ExternalAuth:BaseUrl}/api/internal/validate-admin</c>.
/// </summary>
public sealed class HttpExternalAdminAuthClient(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpExternalAdminAuthClient> logger) : IExternalAdminAuthClient
{
    public const string HttpClientName = "ExternalAdminAuth";

    private const int MaxErrorBodyLength = 512;

    public async Task<ExternalAdminValidationResult?> ValidateAdminAsync(
        string username,
        string password,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.PostAsJsonAsync(
            "/api/internal/validate-admin",
            new ValidateAdminRequest(username, password),
            ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            logger.LogWarning(
                "External auth rejected administrator {Username} with {StatusCode}.",
                username, (int)response.StatusCode);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadErrorBodyAsync(response.Content, ct);

            logger.LogError(
                "External auth failed for {Username} with {StatusCode}. Body: {Body}",
                username, (int)response.StatusCode, body);

            throw new HttpRequestException(
                $"External admin authentication failed with {(int)response.StatusCode} " +
                $"({response.ReasonPhrase}). Body: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);

        var root = document.RootElement;
        var systemId = GetString(root, "system_id", "systemId");
        var canonicalUsername = GetString(root, "username", "user_name") ?? username;
        var piApiKey = GetString(root, "pi_api_key", "piApiKey");

        if (string.IsNullOrWhiteSpace(systemId))
        {
            throw new HttpRequestException(
                "External auth returned 200 OK but no 'system_id' in the response body.");
        }

        logger.LogInformation(
            "External auth accepted administrator {Username} for system {SystemId}.",
            canonicalUsername, systemId);

        return new ExternalAdminValidationResult(systemId, canonicalUsername, piApiKey);
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
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
            return $"<unreadable response body: {ex.GetType().Name}>";
        }
    }

    private sealed record ValidateAdminRequest(string Username, string Password);
}
