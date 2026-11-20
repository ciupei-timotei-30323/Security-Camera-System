namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Shared secret used by the Raspberry Pi when calling the ingestion endpoint.
/// Bound from <c>PiAuth</c> (env var: <c>PiAuth__ApiKey</c>).
/// </summary>
public sealed class PiAuthOptions
{
    public const string SectionName = "PiAuth";

    public string ApiKey { get; set; } = string.Empty;
}
