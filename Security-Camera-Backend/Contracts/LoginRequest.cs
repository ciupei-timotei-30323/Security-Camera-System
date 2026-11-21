namespace Security_Camera_Backend.Contracts;

/// <summary>
/// JSON body accepted by <c>POST /api/auth/login</c>. <see cref="SystemId"/> is optional for
/// factory admins and for sub-user usernames that are unique across systems.
/// </summary>
public sealed record LoginRequest(string? Username, string? Password, string? SystemId = null);
