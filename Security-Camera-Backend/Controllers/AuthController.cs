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
        var response = await authService.LoginAsync(request.Username, request.Password, ct);

        return Ok(response);
    }
}
