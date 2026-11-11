using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Business;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
[Route("api/business-settings")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public sealed class BusinessSettingsController(
    BusinessSettingsService settings, HttpActorResolver actors) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(BusinessSettingsView), StatusCodes.Status200OK)]
    public async Task<ActionResult<BusinessSettingsView>> Get(CancellationToken ct) =>
        Ok(await settings.GetAsync(ct));

    [HttpPut]
    [ProducesResponseType(typeof(BusinessSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BusinessSettingsView>> Update(
        [FromBody] UpdateBusinessLabelsRequest request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await settings.UpdateLabelsAsync(actor, request.DisplayName, request.StaffLabel, ct));
    }

    [HttpPut("time-zone-and-currency")]
    [ProducesResponseType(typeof(BusinessSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BusinessSettingsView>> UpdateTimeZoneAndCurrency(
        [FromBody] UpdateBusinessTimeZoneAndCurrencyRequest request, CancellationToken ct)
    {
        var actor = await actors.ResolveAsync(HttpContext);
        return Ok(await settings.UpdateTimeZoneAndCurrencyAsync(actor, request.TimeZoneId, request.CurrencyCode, ct));
    }
}
