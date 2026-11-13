using System.Globalization;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("api/availability")]
public sealed class AvailabilityController(AvailabilityService availability) : ControllerBase
{
    /// <summary>Lists UTC intervals for a business-local date; this does not reserve a slot.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AvailableSlot>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AvailableSlot>>> List(
        [FromQuery] Guid? staffId, [FromQuery] Guid? staffServiceId,
        [FromQuery] string? date, CancellationToken ct)
    {
        if (staffId is null || staffId == Guid.Empty || staffServiceId is null || staffServiceId == Guid.Empty)
            throw new AppException(ErrorCodes.ValidationFailed,
                "Both staffId and staffServiceId must be nonempty identifiers.", AppErrorKind.Validation);
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var businessDate))
            throw new AppException(ErrorCodes.ValidationFailed,
                "date is required in YYYY-MM-DD format.", AppErrorKind.Validation);
        return Ok(await availability.ListAsync(staffId.Value, staffServiceId.Value, businessDate, ct));
    }
}
