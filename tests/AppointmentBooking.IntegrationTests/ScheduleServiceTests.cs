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
}
