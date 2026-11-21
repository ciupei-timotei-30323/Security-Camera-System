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
}
