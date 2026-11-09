using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Application.Availability;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.Application.Bookings;

public sealed class BookingService
{

    private readonly IBookingRepository _bookings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICatalogRepository _catalog;
    private readonly IUnitOfWork _uow;
    private readonly IStaffOperationLock _lock;
    private readonly AvailabilityService _availability;
    private readonly IClock _clock;
    private readonly IBookingCoordinationRepository _coordinationRepository;

    public BookingService(
        IBookingRepository bookings,
        IServiceScopeFactory scopeFactory,
        ICatalogRepository catalog,
        IUnitOfWork uow,
        IStaffOperationLock staffLock,
        AvailabilityService availability,
        IClock clock,
        IBookingCoordinationRepository coordinationRepository)
    {
        _bookings = bookings;
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _uow = uow;
        _lock = staffLock;
        _availability = availability;
        _clock = clock;
        _coordinationRepository = coordinationRepository;
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
            return await ResolveExisting(existing, request, _catalog, ct);

        try
        {
            return await CreateInsideTransactionAsync(clientUserId, request, ct);
        }
        catch (DbConflictException ex) when (ex.Kind == ConflictKind.UniqueRequestId)
        {
            // Another request with the same (ClientUserId, RequestId) committed first.
            // The transaction was already rolled back and disposed (we are outside its
            // "await using" scope), so read the winner through a FRESH scope/context.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();
            var winner = await repository.GetByClientAndRequestIdAsync(clientUserId, request.RequestId, ct)
                         ?? throw new InvalidOperationException(
                             "Unique violation on RequestId but no row found.");
            return await ResolveExisting(winner, request, catalog, ct);
        }
    }

    /// <summary>Same RequestId already stored: return it if the intent matches, else reject.</summary>
    private async Task<BookingDto> ResolveExisting(
        Booking stored,
        CreateBookingRequest request,
        ICatalogRepository catalog,
        CancellationToken ct)
    {
        var samePayload =
            stored.StaffServiceId == request.StaffServiceId &&
            stored.StartsAtUtc == request.StartAtUtc &&
            stored.Price == request.ExpectedPrice &&
            stored.DurationMinutes == request.ExpectedDurationMinutes;

        if (!samePayload)
            throw new AppException(ErrorCodes.RequestIdConflict, "RequestId already used with different booking details.", AppErrorKind.Conflict);

        return await ToDto(stored, catalog, ct);
    }

    private async Task<BookingDto> CreateInsideTransactionAsync(
        Guid clientUserId, CreateBookingRequest request, CancellationToken ct)
    {

        // 3. Unlocked read, only to learn WHICH employee to lock.
        var preview = await _catalog.GetOfferingByIdAsync(request.StaffServiceId, ct)
                      ?? throw new AppException(ErrorCodes.StaffServiceNotFound, "StaffService not found.", AppErrorKind.NotFound);

        var expectedVersions = await _coordinationRepository.ReadVersionsAsync(preview.StaffMemberId, ct);

        // 4. Transaction + per-employee lock on the same connection.
        await using var tx = await _uow.BeginTransactionAsync(ct);
        await _lock.AcquireAsync(preview.StaffMemberId, ct);

        var existing = await _bookings.GetByClientAndRequestIdAsync(clientUserId, request.RequestId, ct);
        if (existing is not null)
            return await ResolveExisting(existing, request, _catalog, ct);

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
        if (!await _availability.IsAvailableAsync(offering.StaffMemberId, offering.Id, request.StartAtUtc, ct))
            throw new AppException(ErrorCodes.TimeUnavailable, "The requested time is unavailable.", AppErrorKind.Conflict);

        var current = await _coordinationRepository.LockAndReadVersionsAsync(offering.StaffMemberId, ct);
        if (current != expectedVersions)   // or compare each field if it returns several
            throw new AppException(
                ErrorCodes.ConfigurationChanged,
                "Booking settings changed since you started. Please review and confirm again.",
                AppErrorKind.Conflict);


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
            throw new AppException(ErrorCodes.TimeUnavailable, "The requested time is unavailable.", AppErrorKind.Conflict);
        }
        // ConflictKind.UniqueRequestId is NOT caught here: it leaves this method, the
        // transaction is rolled back by disposal, and CreateAsync recovers on a fresh scope.

        // 9. Return only after the commit succeeded.
        return await ToDto(booking, _catalog, ct);
    }

    private async Task<BookingDto> ToDto(Booking b, ICatalogRepository catalog, CancellationToken ct)
    {
        var offering = await catalog.GetOfferingByIdAsync(
            b.StaffServiceId,
            ct)
            ?? throw new InvalidOperationException("Booking has a StaffServiceId that no longer exists.");

        var service = await catalog.GetServiceAsync(offering.ServiceId, ct)
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
