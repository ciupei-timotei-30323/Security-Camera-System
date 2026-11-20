using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Auth;

public sealed record JwtToken(string Token, DateTime ExpiresAtUtc);

public interface IJwtTokenService
{
    JwtToken CreateToken(User user);
}
