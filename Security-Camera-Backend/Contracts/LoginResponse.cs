namespace Security_Camera_Backend.Contracts;

public sealed record LoginUserResponse(int Id, string Username, string Role);

/// <summary>Successful login payload: signed JWT plus the authenticated user.</summary>
public sealed record LoginResponse(string Token, DateTime ExpiresAtUtc, LoginUserResponse User);
