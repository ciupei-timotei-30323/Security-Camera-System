namespace Security_Camera_Backend.Contracts;

/// <summary>
/// Authenticated identity. <see cref="Id"/> is the local database id for sub-users and the
/// built-in admin; it is <c>null</c> for administrators validated by the external factory service.
/// </summary>
public sealed record LoginUserResponse(int? Id, string Username, string Role, string SystemId);

/// <summary>Successful login payload: signed JWT plus the authenticated identity.</summary>
public sealed record LoginResponse(string Token, DateTime ExpiresAtUtc, LoginUserResponse User);
