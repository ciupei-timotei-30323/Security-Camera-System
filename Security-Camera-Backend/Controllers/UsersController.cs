using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Extensions;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Users;

namespace Security_Camera_Backend.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = UserRoles.Admin)]
public sealed class UsersController(IUserManagementService userManagementService) : ControllerBase
{
    /// <summary>Lists the sub-users that belong to the authenticated administrator's system.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UserSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<UserSummaryResponse>>> GetAll(CancellationToken ct)
    {
        var users = await userManagementService.GetAllAsync(User.GetRequiredSystemId(), ct);

        return Ok(users);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(UserSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserSummaryResponse>> GetById(int id, CancellationToken ct)
    {
        var user = await userManagementService.GetByIdAsync(User.GetRequiredSystemId(), id, ct);

        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Creates a sub-user inside the administrator's system. Usernames only need to be unique
    /// within that system, so another tenant may use the same username.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(UserSummaryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserSummaryResponse>> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken ct)
    {
        var created = await userManagementService.CreateAsync(
            User.GetRequiredSystemId(),
            User.GetRequiredUsername(),
            request,
            ct);

        return Created($"/api/users/{created.Id}", created);
    }
}
