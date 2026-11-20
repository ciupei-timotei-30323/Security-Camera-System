namespace Security_Camera_Backend.Contracts;

/// <summary>Profile of the authenticated dashboard user, loaded fresh from the database.</summary>
public sealed record UserProfileResponse(int Id, string Username, string Role, DateTime CreatedAtUtc);
