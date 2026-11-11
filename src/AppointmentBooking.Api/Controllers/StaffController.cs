using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Staff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("api/staff")]
public sealed class StaffController(StaffManagementService staff, HttpActorResolver actors) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<StaffListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<StaffListItem>>> List(
        CancellationToken ct, [FromQuery] bool includeInactive = false)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await staff.BrowseAsync(actor, includeInactive, ct));
    }

    [HttpPost]
    [ProducesResponseType(typeof(StaffProfile), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StaffProfile>> Create(
        [FromBody] CreateStaffRequest request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        var profile = await staff.CreateAsync(actor, request.UserId, request.DisplayName, ct);
        return StatusCode(StatusCodes.Status201Created, profile);
    }

    [HttpPatch("{staffId:guid}")]
    [ProducesResponseType(typeof(StaffProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StaffProfile>> Rename(
        Guid staffId, [FromBody] RenameStaffRequest request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await staff.RenameAsync(actor, staffId, request.DisplayName, ct));
    }

    [HttpPut("{staffId:guid}/activation")]
    [ProducesResponseType(typeof(StaffProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StaffProfile>> SetActive(
        Guid staffId, [FromBody] SetStaffActivationRequest request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await staff.SetActiveAsync(actor, staffId, request.IsActive, ct));
    }
}
