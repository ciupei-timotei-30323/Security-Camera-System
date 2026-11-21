namespace Security_Camera_Backend.Contracts;

/// <summary>Per-system arm/disarm state returned by the settings endpoints.</summary>
public sealed record AwayModeSettingResponse(string SystemId, bool IsArmed, DateTime UpdatedAtUtc);

/// <summary>JSON body accepted by <c>PUT /api/away-mode-settings</c>.</summary>
public sealed record UpdateAwayModeSettingRequest(bool IsArmed);
