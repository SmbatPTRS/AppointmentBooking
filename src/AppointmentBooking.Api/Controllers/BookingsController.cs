using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Bookings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppointmentBooking.Api.Controllers;

[ApiController]
[Authorize(Policy = ApiKeyAuthenticationHandler.SchemeName)]
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

    [HttpGet("mine")]
    [ProducesResponseType(typeof(IReadOnlyList<BookingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<BookingDto>>> Mine(CancellationToken ct)
    {
        var actor = await _actorResolver.ResolveAsync(HttpContext);
        var bookings = await _bookings.ListBookingsByClientAsync(actor, ct);
        return Ok(bookings);
    }

    [HttpGet("/api/staff/{staffMemberId:guid}/bookings")]
    [ProducesResponseType(typeof(IReadOnlyList<BookingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<BookingDto>>> StaffDate(
        Guid staffMemberId,
        [FromQuery] DateOnly dateFrom,
        [FromQuery] DateOnly dateTo,
        CancellationToken ct)
    {
        var actor = await _actorResolver.ResolveAsync(HttpContext);
        return Ok(await _bookings.ListByStaffAndRangeAsync(
            actor, staffMemberId, dateFrom, dateTo, ct));
    }

    [HttpPost("{bookingId:guid}/cancel")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDto>> Cancel(
        Guid bookingId, CancellationToken ct)
    {
        var actor = await _actorResolver.ResolveAsync(HttpContext);
        var booking = await _bookings.CancelAsync(actor, bookingId, ct);
        return booking is null ? NotFound() : Ok(booking);
    }

    [HttpPost("{bookingId:guid}/complete")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDto>> Complete(
        Guid bookingId, CancellationToken ct)
    {
        var actor = await _actorResolver.ResolveAsync(HttpContext);
        var booking = await _bookings.CompleteAsync(actor, bookingId, ct);
        return booking is null ? NotFound() : Ok(booking);
    }
}