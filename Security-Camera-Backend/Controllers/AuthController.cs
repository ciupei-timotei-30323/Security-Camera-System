using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Services.Auth;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    /// <summary>
    /// Authenticates an Admin or User account and returns a signed JWT for dashboard access.
    /// The built-in admin initially logs in with the camera's physical device code.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken ct)
    {
        var response = await authService.LoginAsync(
            request.Username,
            request.Password,
            request.SystemId,
            ct);

        return Ok(response);
    }

    /// <summary>
    /// Revokes the presented JWT server-side; it cannot be used again even though it has not expired.
    /// </summary>
    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [ProducesResponseType(typeof(LogoutResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LogoutResponse>> Logout(CancellationToken ct)
    {
        await authService.LogoutAsync(User, ct);

        return Ok(new LogoutResponse("Logged out successfully."));
    }

    /// <summary>
    /// Returns the current user's profile. The account is re-read from the database so tokens for
    /// deleted users stop working immediately.
    /// </summary>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserProfileResponse>> Me(CancellationToken ct)
    {
        var profile = await authService.GetCurrentUserAsync(User, ct);

        return Ok(profile);
    }
}
