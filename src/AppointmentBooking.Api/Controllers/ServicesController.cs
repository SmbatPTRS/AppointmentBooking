using System.ComponentModel.DataAnnotations;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("api/services")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class ServicesController(CatalogService catalog, HttpActorResolver actors) : ControllerBase
{
    /// <summary>Active shared service types; prices and durations belong to staff offerings.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ServiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ServiceDto>>> List(CancellationToken ct) =>
        Ok(await catalog.ListActiveServicesAsync(ct));

    /// <summary>Creates an active service type. Only the authenticated owner may create it.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ServiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ServiceDto>> Create([FromBody] CreateServiceBody request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        var service = await catalog.CreateServiceAsync(actor, new CreateServiceRequest(request.Name), ct);
        // The public catalogue is the available read resource; there is no GET-by-id route.
        return CreatedAtAction(nameof(List), service);
    }

    /// <summary>Active employee offerings, or an empty array for an unknown/inactive employee.</summary>
    [HttpGet("/api/staff/{staffId:guid}/services")]
    [ProducesResponseType(typeof(IReadOnlyList<StaffServiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<StaffServiceDto>>> ListOfferings(Guid staffId, CancellationToken ct)
    {
        if (staffId == Guid.Empty)
            throw new AppException(ErrorCodes.ValidationFailed, "staffId must be a nonempty identifier.", AppErrorKind.Validation);
        return Ok(await catalog.ListActiveOfferingsAsync(staffId, ct));
    }

    // HTTP model binding validates properties; application DTOs retain their own validation.
    public sealed class CreateServiceBody
    {
        [Required]
        public string Name { get; init; } = string.Empty;
    }
}
