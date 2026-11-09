using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Bookings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/bookings")]
public sealed class BookingsController : ControllerBase
{
    private readonly BookingService _bookings;
    private readonly HttpActorResolver _actorResolver;

    public BookingsController(BookingService bookings, HttpActorResolver actorResolver)
    {
        _bookings = bookings;
        _actorResolver = actorResolver;
    }

    [HttpPost]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    
    public async Task<ActionResult<BookingDto>> Create(
        [FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        var actor = await _actorResolver.ResolveAsync(HttpContext);
        
        var booking = await _bookings.CreateAsync(actor, request, ct);
        return Ok(booking);
    }
}