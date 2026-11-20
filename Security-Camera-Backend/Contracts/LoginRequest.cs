namespace Security_Camera_Backend.Contracts;

/// <summary>JSON body accepted by <c>POST /api/auth/login</c>.</summary>
public sealed record LoginRequest(string? Username, string? Password);
