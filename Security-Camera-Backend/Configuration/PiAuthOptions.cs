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
    /// Optional identity of this Pi/backend. When set, dashboard JWTs whose <c>system_id</c> claim
    /// names a different system are rejected, so a single-Pi node can never be driven by tokens
    /// minted for another camera kit. Leave empty for multi-system deployments.
    /// Env var: <c>PiAuth__NodeSystemId</c>.
    /// </summary>
    public string? NodeSystemId { get; set; }

    /// <summary>
    /// Additional camera kits, keyed by <c>SystemId</c> with the raw API key as the value.
    /// Environment variables use <c>PiAuth__Systems__sys-002</c>.
    /// </summary>
    public Dictionary<string, string> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Alert-terminal keys, keyed by <c>SystemId</c>. These devices may poll
    /// <c>GET /api/device/status</c> but cannot ingest camera frames.
    /// Environment variables use <c>PiAuth__AlertTerminalSystems__sys-001</c>.
    /// </summary>
    public Dictionary<string, string> AlertTerminalSystems { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
