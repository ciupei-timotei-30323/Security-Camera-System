namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Shared secrets used by Raspberry Pis when calling the ingestion endpoint.
/// Bound from <c>PiAuth</c> (env var: <c>PiAuth__ApiKey</c>).
/// </summary>
public sealed class PiAuthOptions
{
    public const string SectionName = "PiAuth";

    public const string DefaultSystemIdValue = "sys-001";

    /// <summary>
    /// Legacy single-key configuration. It is mapped to <see cref="DefaultSystemId"/> so existing
    /// Pis keep working without a config change.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>System that owns <see cref="ApiKey"/>. Defaults to <c>sys-001</c>.</summary>
    public string DefaultSystemId { get; set; } = DefaultSystemIdValue;

    /// <summary>
    /// Additional camera kits, keyed by <c>SystemId</c> with the raw API key as the value.
    /// Environment variables use <c>PiAuth__Systems__sys-002</c>.
    /// </summary>
    public Dictionary<string, string> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
