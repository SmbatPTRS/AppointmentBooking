using System.Text.Json.Serialization;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("api/staff/{staffId:guid}/services")]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class OfferingTermsController(OfferingTermsService terms, HttpActorResolver actors) : ControllerBase
{
    /// <summary>Creates one active offering; an existing staff/service pair returns a conflict.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(StaffServiceDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<StaffServiceDto>> Create(Guid staffId, [FromBody] CreateOfferingBody request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        if (request.ServiceId == Guid.Empty)
            throw new AppException(ErrorCodes.ValidationFailed, "serviceId must be a nonempty identifier.", AppErrorKind.Validation);
        var offering = await terms.CreateAsync(actor, staffId, request.ServiceId,
            new OfferingTermsRequest(request.Price, request.DurationMinutes), ct);
        return CreatedAtAction(nameof(OfferingManagementController.List), "OfferingManagement", new { staffId }, offering);
    }

    /// <summary>Replaces price and duration together; the offering ID and activation status are preserved.</summary>
    [HttpPut("{serviceId:guid}/terms")]
    [ProducesResponseType(typeof(StaffServiceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffServiceDto>> Update(Guid staffId, Guid serviceId,
        [FromBody] UpdateOfferingTermsBody request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await terms.UpdateAsync(actor, staffId, serviceId,
            new OfferingTermsRequest(request.Price, request.DurationMinutes), ct));
    }

    // Explicit JSON presence distinguishes a free offering from an omitted price.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CreateOfferingBody(
        [property: JsonRequired] Guid ServiceId,
        [property: JsonRequired] decimal Price,
        [property: JsonRequired] int DurationMinutes);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record UpdateOfferingTermsBody(
        [property: JsonRequired] decimal Price,
        [property: JsonRequired] int DurationMinutes);
}
