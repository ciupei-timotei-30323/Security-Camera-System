using Security_Camera_Backend.Contracts;

namespace Security_Camera_Backend.Services.Users;

/// <summary>
/// Admin-only management of sub-users. Every lookup and write is scoped to the administrator's
/// own <c>SystemId</c>.
/// </summary>
public interface IUserManagementService
{
    Task<UserSummaryResponse> CreateAsync(
        string systemId,
        string adminUsername,
        CreateUserRequest request,
        CancellationToken ct);

    Task<IReadOnlyList<UserSummaryResponse>> GetAllAsync(string systemId, CancellationToken ct);

    Task<UserSummaryResponse?> GetByIdAsync(string systemId, int userId, CancellationToken ct);

    /// <summary>
    /// Deletes a sub-user in the caller's system. Returns <c>false</c> when the id does not
    /// belong to that system. Throws for self-deletion and for the built-in factory admin.
    /// </summary>
    Task<bool> DeleteAsync(
        string systemId,
        int? requesterUserId,
        string requesterUsername,
        int userId,
        CancellationToken ct);
}
