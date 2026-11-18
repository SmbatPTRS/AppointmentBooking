using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class OfferingManagementController(OfferingManagementService offerings, HttpActorResolver actors) : ControllerBase
{
    /// <summary>Owner sees every employee; active employees see only their own profile.</summary>
    [HttpGet("api/offerings/staff")]
    [ProducesResponseType(typeof(IReadOnlyList<CatalogStaffDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CatalogStaffDto>>> Staff(CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await offerings.ListStaffAsync(actor, ct));
    }

    /// <summary>Authorized catalogue inspection including inactive services and offerings.</summary>
    [HttpGet("api/staff/{staffId:guid}/services/management")]
    [ProducesResponseType(typeof(IReadOnlyList<ManagedOfferingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ManagedOfferingDto>>> List(Guid staffId, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await offerings.ListAsync(actor, staffId, ct));
    }
}
