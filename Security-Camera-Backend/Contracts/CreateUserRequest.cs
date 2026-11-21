namespace Security_Camera_Backend.Contracts;

/// <summary>JSON body accepted by <c>POST /api/users</c> (Admin only).</summary>
public sealed record CreateUserRequest(string? Username, string? Password, string? Role = null);

/// <summary>Sub-user representation safe to return to the dashboard (never includes the hash).</summary>
public sealed record UserSummaryResponse(
    int Id,
    string SystemId,
    string Username,
    string Role,
    DateTime CreatedAtUtc);
