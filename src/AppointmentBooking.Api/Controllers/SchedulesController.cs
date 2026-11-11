using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("api/staff/{staffId:guid}")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public sealed class SchedulesController(ScheduleService schedules, HttpActorResolver actors) : ControllerBase
{
    /// <summary>Returns JSON null if the authorized employee has no configured hours.</summary>
    [HttpGet("schedule")]
    [ProducesResponseType(typeof(WorkingHoursModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHours(Guid staffId, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return new JsonResult(await schedules.GetHoursAsync(actor, staffId, ct));
    }

    [HttpPut("schedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetHours(Guid staffId, [FromBody] WorkingHoursModel request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        await schedules.SetHoursAsync(actor, staffId, request, ct);
        return NoContent();
    }

    [HttpGet("days-off")]
    [ProducesResponseType(typeof(IReadOnlyList<DayOffDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DayOffDto>>> ListDays(Guid staffId, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await schedules.ListDaysOffAsync(actor, staffId, ct));
    }

    [HttpPost("days-off")]
    [ProducesResponseType(typeof(DayOffDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DayOffDto>> AddDay(Guid staffId, [FromBody] DayOffModel request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        var day = await schedules.AddDayOffAsync(actor, staffId, request, ct);
        return CreatedAtAction(nameof(ListDays), new { staffId }, day);
    }

    [HttpDelete("days-off/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveDay(Guid staffId, Guid id, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        await schedules.RemoveDayOffAsync(actor, staffId, id, ct);
        return NoContent();
    }
}
