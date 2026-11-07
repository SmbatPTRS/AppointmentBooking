using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;

namespace AppointmentBooking.Application.Bookings;

// =============================================================================
// ASSUMED CONTRACTS - compare with the real repo and rename to match.
//
//   IBookingRepository     : GetByClientRequestIdAsync(clientUserId, requestId, ct), Add(booking)
//   ICatalogRepository     : GetOfferingAsync(staffServiceId, ct) -> offering with StaffMember + Service
//                            (a tracked/fresh read; must not return a stale cached copy)
//   IBookingReader         : same lookup as above but on a FRESH scope/context (used only after a
//                            unique-key conflict, because the old transaction is aborted)
//   IUnitOfWork            : BeginTransactionAsync(ct) -> IAsyncDisposable with CommitAsync(ct),
//                            SaveChangesAsync(ct). SaveChangesAsync translates PostgreSQL errors:
//                              23505 on (ClientUserId, RequestId) -> DbConflictException(UniqueRequestId)
//                              23P01 exclusion (overlap)          -> DbConflictException(Overlap)
//   IStaffOperationLock    : AcquireAsync(staffMemberId, ct) - transaction-scoped advisory lock,
//                            on the SAME connection as the transaction
//   IAvailabilityService   : ValidateIntervalAsync(staffServiceId, startUtc, ct) (B, Task 09)
//   IClock                 : UtcNow
//   AppException(code)     : A's exception; ErrorCodes.* from A
//   Booking entity (A)     : needs RequestId and DurationMinutes (ask A to add them)
// =============================================================================


public sealed class BookingService
{

    private readonly IBookingRepository _bookings;
    private readonly IBookingReader _freshReader;
    private readonly ICatalogRepository _catalog;
    private readonly IUnitOfWork _uow;
    private readonly IStaffOperationLock _lock;
    private readonly IAvailabilityService _availability;
    private readonly IClock _clock;

    public BookingService(
        IBookingRepository bookings,
        IBookingReader freshReader,
        ICatalogRepository catalog,
        IUnitOfWork uow,
        IStaffOperationLock staffLock,
        IAvailabilityService availability,
        IClock clock)
    {
        _bookings = bookings;
        _freshReader = freshReader;
        _catalog = catalog;
        _uow = uow;
        _lock = staffLock;
        _availability = availability;
        _clock = clock;
    }

    public async Task<BookingDto> CreateAsync(
        Actor actor, CreateBookingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        // 0. Cheap input checks (no database).
        if (request.RequestId == Guid.Empty)
            throw new AppException(ErrorCodes.ValidationFailed, "RequestId is required.", AppErrorKind.Validation);
        if (request.StartAtUtc.Kind != DateTimeKind.Utc)
            throw new AppException(ErrorCodes.ValidationFailed, "StartAtUtc must be in UTC.", AppErrorKind.Validation);
        if (request.ExpectedPrice < 0 || request.ExpectedDurationMinutes <= 0)
            throw new AppException(ErrorCodes.ValidationFailed, "Expected price and duration must be positive.", AppErrorKind.Validation);

        // 1. Identity comes from the actor, never from the request.
        var clientUserId = actor.UserId;

        // 2. Idempotent retry: runs BEFORE any eligibility check, so a retry of a
        //    booking that already succeeded never fails because the slot is now taken.
        var existing = await _bookings.GetByClientAndRequestIdAsync(clientUserId, request.RequestId, ct);
        if (existing is not null)
            return await ResolveExisting(existing, request, ct);

        try
        {
            return await CreateInsideTransactionAsync(clientUserId, request, ct);
        }
        catch (DbConflictException ex) when (ex.Kind == ConflictKind.UniqueRequestId)
        {
            // Another request with the same (ClientUserId, RequestId) committed first.
            // The transaction was already rolled back and disposed (we are outside its
            // "await using" scope), so read the winner through a FRESH scope/context.
            var winner = await _freshReader.GetByClientRequestIdAsync(clientUserId, request.RequestId, ct)
                         ?? throw new InvalidOperationException(
                             "Unique violation on RequestId but no row found.");
            return await ResolveExisting(winner, request, ct);
        }
    }

    /// <summary>Same RequestId already stored: return it if the intent matches, else reject.</summary>
    private async Task<BookingDto> ResolveExisting(
        Booking stored,
        CreateBookingRequest request,
        CancellationToken ct)
    {
        var samePayload =
            stored.StaffServiceId == request.StaffServiceId &&
            stored.StartsAtUtc == request.StartAtUtc &&
            stored.Price == request.ExpectedPrice &&
            stored.DurationMinutes == request.ExpectedDurationMinutes;

        if (!samePayload)
            throw new AppException(ErrorCodes.RequestIdConflict, "RequestId already used with different booking details.", AppErrorKind.Conflict);

        return await ToDto(stored, ct);
    }

    private async Task<BookingDto> CreateInsideTransactionAsync(
        Guid clientUserId, CreateBookingRequest request, CancellationToken ct)
    {
        // 3. Unlocked read, only to learn WHICH employee to lock.
        var preview = await _catalog.GetOfferingByIdAsync(request.StaffServiceId, ct)
                      ?? throw new AppException(ErrorCodes.StaffServiceNotFound, "StaffService not found.", AppErrorKind.NotFound);

        // 4. Transaction + per-employee lock on the same connection.
        await using var tx = await _uow.BeginTransactionAsync(ct);
        await _lock.AcquireAsync(preview.StaffMemberId, ct);

        // 5. Re-read EVERYTHING inside the lock; the unlocked read may be stale.
        var offering = await _bookings.GetBookingOfferingAsync(request.StaffServiceId, ct)
                       ?? throw new AppException(ErrorCodes.StaffServiceNotFound, "StaffService not found.", AppErrorKind.NotFound);

        if (!offering.IsActive || !offering.IsServiceActive || !offering.IsStaffMemberActive)
            throw new AppException(ErrorCodes.OfferingInactive, "StaffService or its StaffMember or Service is inactive.", AppErrorKind.Conflict);

        // 6. The customer must have seen the price and duration that apply now.
        if (offering.Price != request.ExpectedPrice || offering.DurationMinutes != request.ExpectedDurationMinutes)
            throw new AppException(ErrorCodes.OfferingChanged, "Price or duration has changed since the booking process started.", AppErrorKind.Conflict);

        // 7. Exact-interval validation (working hours, breaks, days off, slot grid, not in the past).
        //    B's method also reads business settings (time zone) itself.
        if (request.StartAtUtc <= _clock.UtcNow)
            throw new AppException(ErrorCodes.TimeUnavailable, "Booking time must be in the future.", AppErrorKind.Conflict);
        await _availability.ValidateIntervalAsync(request.StaffServiceId, request.StartAtUtc, ct);

        // 8. Snapshot values come from the SERVER's offering, not from the request.
        var booking = new Booking(
            clientUserId,
            offering.StaffMemberId,
            offering.Id,
            request.RequestId,
            request.StartAtUtc,
            offering.Price,
            offering.DurationMinutes,
            _clock.UtcNow);

        _bookings.Add(booking);

        try
        {
            await _uow.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbConflictException ex) when (ex.Kind == ConflictKind.Overlap)
        {
            // Last line of defence behind the lock: the exclusion constraint fired.
            throw new AppException(ErrorCodes.TimeUnavailable);
        }
        // ConflictKind.UniqueRequestId is NOT caught here: it leaves this method, the
        // transaction is rolled back by disposal, and CreateAsync recovers on a fresh scope.

        // 9. Return only after the commit succeeded.
        return await ToDto(booking, ct);
    }

    private async Task<BookingDto> ToDto(Booking b, CancellationToken ct)
    {
        var offering = await _catalog.GetOfferingByIdAsync(
            b.StaffServiceId,
            ct)
            ?? throw new InvalidOperationException("Booking has a StaffServiceId that no longer exists.");

        var service = await _catalog.GetServiceAsync(offering.ServiceId, ct)
            ?? throw new InvalidOperationException("StaffService has a ServiceId that no longer exists.");

        return new BookingDto(
            b.Id,
            b.StaffServiceId,
            service.Name,
            b.Price,
            b.DurationMinutes,
            b.StartsAtUtc,
            b.EndsAtUtc,
            b.Status
        );
    }

}