using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleServiceTests(PostgresFixture fixture)
{
    private sealed class Access(bool deny) : IAccessService
    {
        public Task RequireStaffManagementAsync(Actor actor, Guid id, CancellationToken ct) =>
            deny ? throw new AppException(ErrorCodes.Forbidden, "Denied", AppErrorKind.Forbidden) : Task.CompletedTask;
        public Task RequireOwnerAsync(Actor actor, CancellationToken ct) => throw new NotSupportedException();
        public Task RequireAppointmentManagementAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HoursEditPreservesFutureBookingsAndHonorsAccess(bool conflict, bool deny)
    {
        await using var db = fixture.CreateContext();
        var clock = new Clock();
        var user = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Schedule test", null, clock.UtcNow);
        var staff = new StaffMember(user.Id, "Schedule test");
        var service = new Service("Schedule test");
        var offering = new StaffService(staff.Id, service.Id, 100m, 30);
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null) db.BusinessSettings.Add(new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings?.TimeZoneId ?? "UTC");
        var start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2027, 2, 1, 16, 0, 0), zone);
        var hours = new WorkingHours(staff.Id, new(9, 0), new(18, 0));
        db.AddRange(user, staff, service, offering, hours,
            new Booking(user.Id, staff.Id, offering.Id, Guid.NewGuid(), start, 100m, 30, clock.UtcNow));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var sut = new ScheduleService(new Access(deny), new ScheduleRepository(db),
            new BookingRepository(db), new BusinessSettingsRepository(db), db,
            new PostgresStaffOperationLock(db), clock, new ScheduleChangeValidator());
        var proposed = new WorkingHoursModel(new(10, 0), conflict ? new(16, 0) : new(17, 0));
        if (conflict || deny)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => sut.SetHoursAsync(new Actor(user.Id), staff.Id, proposed, default));
            Assert.Equal(deny ? ErrorCodes.Forbidden : ErrorCodes.ScheduleConflictsWithBookings, ex.Code);
        }
        else await sut.SetHoursAsync(new Actor(user.Id), staff.Id, proposed, default);
        foreach (var dayOff in new[] { new DayOffModel(DayOfWeek.Monday), new DayOffModel(Date: new(2027, 2, 1)) })
        {
            var dayError = await Assert.ThrowsAsync<AppException>(() => sut.AddDayOffAsync(new Actor(user.Id), staff.Id, dayOff, default));
            Assert.Equal(deny ? ErrorCodes.Forbidden : ErrorCodes.ScheduleConflictsWithBookings, dayError.Code);
        }
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == staff.Id));
        await using var observer = fixture.CreateContext();
        var saved = await observer.WorkingHours.SingleAsync(h => h.StaffMemberId == staff.Id);
        Assert.Equal(conflict || deny ? new TimeOnly(9, 0) : new TimeOnly(10, 0), saved.OpensAt);
        Assert.Equal(hours.Id, saved.Id);
        Assert.Equal(conflict || deny ? new TimeOnly(18, 0) : new TimeOnly(17, 0), saved.ClosesAt);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DaysOffCanBeListedAndRemovedButNotDuplicated(bool recurring)
    {
        await using var db = fixture.CreateContext();
        var clock = new Clock();
        var user = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Days test", null, clock.UtcNow);
        var staff = new StaffMember(user.Id, "Days test");
        db.AddRange(user, staff);
        await db.SaveChangesAsync();
        var sut = new ScheduleService(new Access(false), new ScheduleRepository(db),
            new BookingRepository(db), new BusinessSettingsRepository(db), db,
            new PostgresStaffOperationLock(db), clock, new ScheduleChangeValidator());
        var actor = new Actor(user.Id);
        var proposed = recurring ? new DayOffModel(DayOfWeek.Monday) : new DayOffModel(Date: new(2027, 2, 1));
        var added = await sut.AddDayOffAsync(actor, staff.Id, proposed, default);
        Assert.Equal(proposed.Weekday, added.Weekday);
        Assert.Equal(proposed.Date, added.Date);
        Assert.Equal(added, Assert.Single(await sut.ListDaysOffAsync(actor, staff.Id, default)));
        var duplicate = await Assert.ThrowsAsync<AppException>(() => sut.AddDayOffAsync(actor, staff.Id, proposed, default));
        Assert.Equal(ErrorCodes.DuplicateDayOff, duplicate.Code);
        Assert.Equal(AppErrorKind.Conflict, duplicate.Kind);
        await sut.RemoveDayOffAsync(actor, staff.Id, added.Id, default);
        Assert.Empty(await sut.ListDaysOffAsync(actor, staff.Id, default));
        var missing = await Assert.ThrowsAsync<AppException>(() => sut.RemoveDayOffAsync(actor, staff.Id, added.Id, default));
        Assert.Equal(ErrorCodes.DayOffNotFound, missing.Code);
    }
    private sealed class ObservedLock(IStaffOperationLock inner) : IStaffOperationLock
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AcquireAsync(Guid staffId, CancellationToken ct)
        {
            var acquisition = inner.AcquireAsync(staffId, ct);
            Entered.TrySetResult();
            await acquisition;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationObservesBookingCommittedWhileWaitingForEmployeeLock(bool dayOff)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var writer = fixture.CreateContext();
        var clock = new Clock();
        var user = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Race", null, clock.UtcNow);
        var staff = new StaffMember(user.Id, "Race");
        var service = new Service("Race");
        var offering = new StaffService(staff.Id, service.Id, 100m, 30);
        var settings = await writer.BusinessSettings.SingleOrDefaultAsync(ct);
        if (settings is null) writer.BusinessSettings.Add(new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings?.TimeZoneId ?? "UTC");
        var start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2027, 2, 1, 16, 0, 0), zone);
        writer.AddRange(user, staff, service, offering, new WorkingHours(staff.Id, new(9, 0), new(18, 0)));
        await writer.SaveChangesAsync(ct);
        await using var transaction = await writer.BeginTransactionAsync(ct);
        await new PostgresStaffOperationLock(writer).AcquireAsync(staff.Id, ct);

        await using var editor = fixture.CreateContext();
        var observed = new ObservedLock(new PostgresStaffOperationLock(editor));
        var sut = new ScheduleService(new Access(false), new ScheduleRepository(editor),
            new BookingRepository(editor), new BusinessSettingsRepository(editor), editor,
            observed, clock, new ScheduleChangeValidator());
        Task edit = dayOff
            ? sut.AddDayOffAsync(new Actor(user.Id), staff.Id, new DayOffModel(Date: new(2027, 2, 1)), ct)
            : sut.SetHoursAsync(new Actor(user.Id), staff.Id, new WorkingHoursModel(new(9, 0), new(16, 0)), ct);
        await observed.Entered.Task.WaitAsync(ct);
        // Observe PostgreSQL itself waiting: no timing-based sleep assertion.
        while (!await writer.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted) AS \"Value\"")
            .SingleAsync(ct))
        {
            if (edit.IsCompleted) throw new InvalidOperationException("Schedule edit did not wait for the employee lock.");
            await Task.Delay(10, ct);
        }
        writer.Bookings.Add(new Booking(user.Id, staff.Id, offering.Id, Guid.NewGuid(), start, 100m, 30, clock.UtcNow));
        await writer.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var error = await Assert.ThrowsAsync<AppException>(() => edit);
        Assert.Equal(ErrorCodes.ScheduleConflictsWithBookings, error.Code);
        await using var verification = fixture.CreateContext();
        Assert.Equal(new TimeOnly(18, 0), (await verification.WorkingHours.SingleAsync(h => h.StaffMemberId == staff.Id, ct)).ClosesAt);
        Assert.False(await verification.StaffDaysOff.AnyAsync(d => d.StaffMemberId == staff.Id, ct));
    }

    [Fact]
    public async Task InvalidInputReturnsApplicationErrorsWithoutSaving()
    {
        await using var db = fixture.CreateContext();
        var sut = new ScheduleService(new Access(false), new ScheduleRepository(db),
            new BookingRepository(db), new BusinessSettingsRepository(db), db,
            new PostgresStaffOperationLock(db), new Clock(), new ScheduleChangeValidator());
        var actor = new Actor(Guid.NewGuid());
        var staffId = Guid.NewGuid();
        foreach (var hours in new[] { new WorkingHoursModel(new(18, 0), new(9, 0)),
            new WorkingHoursModel(new(9, 0), new(18, 0), new(12, 0)),
            new WorkingHoursModel(new(9, 0), new(18, 0), new(8, 0), new(10, 0)) })
        {
            var error = await Assert.ThrowsAsync<AppException>(() => sut.SetHoursAsync(actor, staffId, hours, default));
            Assert.Equal(ErrorCodes.InvalidWorkingHours, error.Code);
            Assert.Equal(AppErrorKind.Validation, error.Kind);
        }
        foreach (var day in new[] { new DayOffModel(), new DayOffModel((DayOfWeek)7),
            new DayOffModel(DayOfWeek.Monday, new(2027, 2, 1)) })
        {
            var error = await Assert.ThrowsAsync<AppException>(() => sut.AddDayOffAsync(actor, staffId, day, default));
            Assert.Equal(ErrorCodes.InvalidDayOff, error.Code);
            Assert.Equal(AppErrorKind.Validation, error.Kind);
        }
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }
    // Uses tracked reads like the shared StaffRepository: a pre-lock check would
    // cache active state and a repeated query alone would not refresh it.
    private sealed class DatabaseAccess(AppointmentBooking.Infrastructure.Persistence.AppDbContext db) : IAccessService
    {
        public async Task RequireStaffManagementAsync(Actor actor, Guid staffId, CancellationToken ct)
        {
            var staff = await db.StaffMembers.SingleAsync(s => s.Id == staffId, ct);
            if (staff.UserId != actor.UserId)
                throw new AppException(ErrorCodes.Forbidden, "Not this employee.", AppErrorKind.Forbidden);
            if (!staff.IsActive)
                throw new AppException(ErrorCodes.StaffInactive, "Inactive employee.", AppErrorKind.Forbidden);
        }
        public Task RequireOwnerAsync(Actor actor, CancellationToken ct) => throw new NotSupportedException();
        public Task RequireAppointmentManagementAsync(Actor actor, Guid staffId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeactivationBeforeLockAcquisitionDeniesEveryWrite(int operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var writer = fixture.CreateContext();
        var clock = new Clock();
        var user = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Race", null, clock.UtcNow);
        var staff = new StaffMember(user.Id, "Race");
        var service = new Service("Race");
        var offering = new StaffService(staff.Id, service.Id, 100m, 30);
        var settings = await writer.BusinessSettings.SingleOrDefaultAsync(ct);
        if (settings is null) writer.BusinessSettings.Add(new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings?.TimeZoneId ?? "UTC");
        var start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2027, 2, 1, 16, 0, 0), zone);
        writer.AddRange(user, staff, service, offering, new WorkingHours(staff.Id, new(9, 0), new(18, 0)));
        var day = new StaffDayOff(staff.Id, DayOfWeek.Sunday, null);
        writer.Add(day);
        await writer.SaveChangesAsync(ct);
        await using var transaction = await writer.BeginTransactionAsync(ct);
        await new PostgresStaffOperationLock(writer).AcquireAsync(staff.Id, ct);

        await using var editor = fixture.CreateContext();
        var observed = new ObservedLock(new PostgresStaffOperationLock(editor));
        var sut = new ScheduleService(new DatabaseAccess(editor), new ScheduleRepository(editor),
            new BookingRepository(editor), new BusinessSettingsRepository(editor), editor,
            observed, clock, new ScheduleChangeValidator());
        Task edit = operation switch
        {
            0 => sut.SetHoursAsync(new Actor(user.Id), staff.Id, new WorkingHoursModel(new(10, 0), new(17, 0)), ct),
            1 => sut.AddDayOffAsync(new Actor(user.Id), staff.Id, new DayOffModel(DayOfWeek.Monday), ct),
            _ => sut.RemoveDayOffAsync(new Actor(user.Id), staff.Id, day.Id, ct)
        };
        await observed.Entered.Task.WaitAsync(ct);
        // Observe PostgreSQL itself waiting: no timing-based sleep assertion.
        while (!await writer.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted) AS \"Value\"")
            .SingleAsync(ct))
        {
            if (edit.IsCompleted) throw new InvalidOperationException("Schedule edit did not wait for the employee lock.");
            await Task.Delay(10, ct);
        }
        staff.Deactivate();
        await writer.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var error = await Assert.ThrowsAsync<AppException>(() => edit);
        Assert.Equal(ErrorCodes.StaffInactive, error.Code);
        Assert.Equal(AppErrorKind.Forbidden, error.Kind);
        Assert.Null(editor.Database.CurrentTransaction);
        await using var verification = fixture.CreateContext();
        Assert.Equal(new TimeOnly(18, 0), (await verification.WorkingHours.SingleAsync(h => h.StaffMemberId == staff.Id, ct)).ClosesAt);
        Assert.Equal(day.Id, (await verification.StaffDaysOff.SingleAsync(d => d.StaffMemberId == staff.Id, ct)).Id);
    }

}
