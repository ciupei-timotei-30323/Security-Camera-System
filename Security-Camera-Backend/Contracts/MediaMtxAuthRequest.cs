namespace Security_Camera_Backend.Contracts;

/// <summary>
/// Payload posted by MediaMTX to <c>POST /api/internal/mediamtx-auth</c> when
/// <c>authMethod: http</c> is configured. MediaMTX sends the fields it knows about; unknown ones
/// are ignored by the JSON binder.
/// </summary>
/// <remarks>
/// See https://github.com/bluenviron/mediamtx#authentication for the canonical shape. MediaMTX
/// answers the request with 200 for "allowed" and 401 for "denied"; the body is ignored.
/// </remarks>
public sealed record MediaMtxAuthRequest(
    string? User = null,
    string? Password = null,
    string? Token = null,
    string? Ip = null,
    string? Action = null,
    string? Path = null,
    string? Protocol = null,
    string? Id = null,
    string? Query = null);

/// <summary>
/// Response body MediaMTX understands for <c>read</c> actions. <c>expireAt</c> lets MediaMTX stop
/// serving a stream once the short-lived stream ticket expires, without waiting for the player to
/// disconnect.
/// </summary>
public sealed record MediaMtxAuthResponse(string? ExpireAt);
