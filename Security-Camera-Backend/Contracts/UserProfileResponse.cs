namespace Security_Camera_Backend.Contracts;

/// <summary>
/// Profile of the authenticated dashboard user. Sub-users are loaded fresh from the database so
/// deleted accounts stop working immediately; external factory admins are claim-backed and have
/// no <see cref="Id"/> or <see cref="CreatedAtUtc"/>.
/// </summary>
public sealed record UserProfileResponse(
    int? Id,
    string Username,
    string Role,
    string SystemId,
    DateTime? CreatedAtUtc);
