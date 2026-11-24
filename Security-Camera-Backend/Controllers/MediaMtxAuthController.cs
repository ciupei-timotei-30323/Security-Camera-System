using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Services.Mqtt;

namespace Security_Camera_Backend.Controllers;

/// <summary>
/// MediaMTX external authorization hook. MediaMTX posts here for every <c>publish</c> and
/// <c>read</c> attempt; a 200 means "allowed", 401 means "denied".
///
/// This endpoint is intentionally anonymous: the caller IS MediaMTX, and the credentials under
/// test travel in the request body. It must never be reachable from the public internet, so the
/// route lives under <c>/api/internal</c> and compose keeps the backend on the private network.
/// </summary>
[ApiController]
[Route("api/internal/mediamtx-auth")]
[AllowAnonymous]
public sealed class MediaMtxAuthController(
    MediaMtxPathAuthorizer authorizer,
    ILogger<MediaMtxAuthController> logger) : ControllerBase
{
    /// <summary>
    /// Authorizes a single MediaMTX action. MediaMTX only inspects the status code, but the body
    /// carries <c>expireAt</c> for <c>read</c> so it can drop a stream when the ticket expires.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Authorize(
        [FromBody] MediaMtxAuthRequest request,
        CancellationToken ct)
    {
        var decision = await authorizer.AuthorizeAsync(
            request.Action,
            request.Path,
            request.Password,
            request.Token,
            request.Query,
            ct);

        if (!decision.Allowed)
        {
            logger.LogInformation(
                "MediaMTX denied action {Action} on path {Path} from {Ip}: {Reason}",
                request.Action, request.Path, request.Ip, decision.Reason);

            return Unauthorized();
        }

        logger.LogDebug(
            "MediaMTX allowed action {Action} on path {Path} from {Ip}.",
            request.Action, request.Path, request.Ip);

        var expireAt = decision.ExpireAt?.ToUniversalTime().ToString("O");

        return Ok(new MediaMtxAuthResponse(expireAt));
    }
}
