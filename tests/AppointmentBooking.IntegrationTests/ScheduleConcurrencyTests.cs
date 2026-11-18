using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleConcurrencyTests(PostgresFixture fixture)
{
    private static long nextTelegramUserId = 3000000000;

    [Fact]
    public async Task TransactionCommitPersistsChanges()
    {
        await using var db = fixture.CreateContext();
        var user = new User(
            Interlocked.Increment(ref nextTelegramUserId),
            "Committed user",
            null,
            DateTime.UtcNow);

        await using (var transaction = await db.BeginTransactionAsync(CancellationToken.None))
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using var verification = fixture.CreateContext();
        Assert.NotNull(await verification.Users.SingleOrDefaultAsync(item => item.Id == user.Id));
    }

    [Fact]
    public async Task DisposingAnUncommittedTransactionRollsBackChanges()
    {
        var userId = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync(CancellationToken.None);
            db.Database.ExecuteSqlInterpolated(
                $"INSERT INTO users (id, telegram_user_id, display_name, created_at_utc) VALUES ({userId}, {Interlocked.Increment(ref nextTelegramUserId)}, {"Rolled back"}, {DateTime.UtcNow})");
            await transaction.DisposeAsync();
        }

        await using var verification = fixture.CreateContext();
        Assert.Null(await verification.Users.SingleOrDefaultAsync(item => item.Id == userId));
    }

    [Fact]
    public async Task AContextCannotBeginTwoTransactions()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.BeginTransactionAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.BeginTransactionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BookingAndAddingDayOffSerializeOnTheSameEmployee()
    {
        await using var writer = fixture.CreateContext();
        var clock = new TestClock();
        await new DatabaseSeeder(writer).SeedBusinessSettingsAsync(
            new BusinessSettings("Studio", "Staff", "UTC", "AMD"), CancellationToken.None);
        var employee = new User(Interlocked.Increment(ref nextTelegramUserId), "Race employee", null, clock.UtcNow);
        var client = new User(Interlocked.Increment(ref nextTelegramUserId), "Race client", null, clock.UtcNow);
        var staff = new StaffMember(employee.Id, "Race staff");
        var service = new Service("Race service");
        var offering = new StaffService(staff.Id, service.Id, 25m, 30);
        writer.AddRange(employee, client, staff, service, offering, new BookingCoordination(staff),
            new WorkingHours(staff.Id, new TimeOnly(9, 0), new TimeOnly(17, 0)));
        await writer.SaveChangesAsync();

        await using var transaction = await writer.BeginTransactionAsync(CancellationToken.None);
        await new PostgresStaffOperationLock(writer).AcquireAsync(staff.Id, CancellationToken.None);

        await using var editor = fixture.CreateContext();
        var observed = new ObservedLock(new PostgresStaffOperationLock(editor));
        var schedule = new ScheduleService(new AllowStaffAccess(), new ScheduleRepository(editor),
            new BookingRepository(editor, clock), new BusinessSettingsRepository(editor), editor,
            observed, clock, new ScheduleChangeValidator());
        var day = new DateOnly(2027, 2, 1);
        var addingDayOff = schedule.AddDayOffAsync(new Actor(employee.Id), staff.Id,
            new DayOffModel(Date: day), CancellationToken.None);
        await observed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var start = new DateTime(2027, 2, 1, 10, 0, 0, DateTimeKind.Utc);
        writer.Bookings.Add(new Booking(client.Id, staff.Id, offering.Id, Guid.NewGuid(), start, 25m, 30, clock.UtcNow));
        await writer.SaveChangesAsync();
        await transaction.CommitAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<AppException>(() => addingDayOff);
        Assert.Equal(ErrorCodes.ScheduleConflictsWithBookings, error.Code);
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.StaffDaysOff.AnyAsync(d => d.StaffMemberId == staff.Id));
        Assert.Single(await verification.Bookings.Where(b => b.StaffMemberId == staff.Id).ToListAsync());
    }

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    }

    private sealed class ObservedLock(IStaffOperationLock inner) : IStaffOperationLock
    {
        public TaskCompletionSource Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
        {
            Requested.TrySetResult();
            return inner.AcquireAsync(staffMemberId, ct);
        }
    }

    private sealed class AllowStaffAccess : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) => Task.FromResult(false);
        public Task RequireOwnerAsync(Actor actor, CancellationToken ct) => Task.CompletedTask;
        public Task RequireStaffManagementAsync(Actor actor, Guid staffId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RequireAppointmentManagementAsync(Actor actor, Guid staffId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
