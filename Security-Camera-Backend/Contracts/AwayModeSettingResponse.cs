namespace Security_Camera_Backend.Contracts;

/// <summary>Per-system away-mode configuration returned by the settings endpoints.</summary>
public sealed record AwayModeSettingResponse(
    string SystemId,
    string Mode,
    string? StartTime,
    string? EndTime,
    string? AlertEmail,
    string TimeZone,
    bool IsArmedNow,
    bool IsArmed,
    DateTime UpdatedAtUtc);

/// <summary>
/// JSON body accepted by <c>PUT /api/away-mode-settings</c> and
/// <c>PUT /api/settings/away-mode</c>. Both the new shape and the legacy <c>{ "isArmed": bool }</c>
/// shape are accepted.
/// </summary>
public sealed record UpdateAwayModeSettingRequest(
    string? Mode = null,
    string? StartTime = null,
    string? EndTime = null,
    string? AlertEmail = null,
    string? TimeZone = null,
    bool? IsArmed = null);

/// <summary>One page of entrance logs, newest first.</summary>
public sealed record PagedEventsResponse(
    IReadOnlyList<DetectionEventResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Validated query parameters for <c>GET /api/events</c>.</summary>
public sealed record EventQuery(
    int Page = 1,
    int PageSize = 20,
    bool SuspectOnly = false,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);
