using Security_Camera_Backend.Contracts;

namespace Security_Camera_Backend.Services.Auth;

public interface IAuthService
{
    /// <summary>
    /// Validates credentials and issues a JWT. Throws <see cref="Exceptions.ValidationAppException"/>
    /// for missing input and <see cref="Exceptions.UnauthorizedAppException"/> for invalid credentials.
    /// </summary>
    Task<LoginResponse> LoginAsync(string? username, string? password, CancellationToken ct);
}
