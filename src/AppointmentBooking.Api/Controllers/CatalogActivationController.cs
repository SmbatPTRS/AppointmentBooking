using System.Text.Json.Serialization;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class CatalogActivationController(SharedServiceActivationService services,
    OfferingActivationService offerings, HttpActorResolver actors) : ControllerBase
{
    [HttpPut("api/services/{serviceId:guid}/activation")]
    [ProducesResponseType(typeof(ServiceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ServiceDto>> Service(Guid serviceId, [FromBody] ActivationBody request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await services.SetActiveAsync(actor, serviceId, request.IsActive, ct));
    }

    [HttpPut("api/staff/{staffId:guid}/services/{serviceId:guid}/activation")]
    [ProducesResponseType(typeof(StaffServiceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffServiceDto>> Offering(Guid staffId, Guid serviceId,
        [FromBody] ActivationBody request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await offerings.SetActiveAsync(actor, staffId, serviceId, request.IsActive, ct));
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ActivationBody([property: JsonRequired] bool IsActive);
}
