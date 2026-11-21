namespace Security_Camera_Backend.Contracts;

/// <summary>Status of one physical device type for the caller's system.</summary>
public sealed record DeviceStatusResponse(bool Online, DateTimeOffset? LastSeen);

/// <summary>Aggregated dashboard status for the caller's system.</summary>
public sealed record SystemStatusResponse(
    DeviceStatusResponse Camera,
    DeviceStatusResponse AlertTerminal,
    string Backend,
    string CvService);
