using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Time;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BookingConcurrencyTests(PostgresFixture fixture)
{
    private static long nextTelegramUserId = 2000000000;

    [Fact]
    public async Task StaffLockBlocksAnotherTransactionUntilTheFirstCommits()
    {
        var staffId = Guid.NewGuid();
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var firstTransaction = await first.BeginTransactionAsync(CancellationToken.None);
        await using var secondTransaction = await second.BeginTransactionAsync(CancellationToken.None);

        var firstLock = new PostgresStaffOperationLock(first);
        var secondLock = new PostgresStaffOperationLock(second);
        await firstLock.AcquireAsync(staffId, CancellationToken.None);

        var secondAcquire = secondLock.AcquireAsync(staffId, CancellationToken.None);
        await AssertBlockedAsync(secondAcquire);

        await firstTransaction.CommitAsync(CancellationToken.None);
        await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await secondTransaction.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LocksForDifferentStaffMembersDoNotBlockEachOther()
    {
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var firstTransaction = await first.BeginTransactionAsync(CancellationToken.None);
        await using var secondTransaction = await second.BeginTransactionAsync(CancellationToken.None);

        await new PostgresStaffOperationLock(first)
            .AcquireAsync(Guid.NewGuid(), CancellationToken.None);

        var secondAcquire = new PostgresStaffOperationLock(second)
            .AcquireAsync(Guid.NewGuid(), CancellationToken.None);

        await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await firstTransaction.CommitAsync(CancellationToken.None);
        await secondTransaction.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaffLockRequiresAnActiveTransactionAndNonEmptyStaffId()
    {
        await using var db = fixture.CreateContext();
        var lockService = new PostgresStaffOperationLock(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => lockService.AcquireAsync(Guid.Empty, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lockService.AcquireAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task BookingRepositoryReturnsOnlyConfirmedOverlapsAndFutureBookings()
    {
        await using var db = fixture.CreateContext();
        var seed = AddBookingDependencies(db);
        var now = DateTime.UtcNow;
        var overlapping = new Booking(
            seed.ClientId, seed.StaffId, seed.StaffServiceId, Guid.NewGuid(),
            now.AddHours(1), 25m, 30, now);
        var nonOverlapping = new Booking(
            seed.ClientId, seed.StaffId, seed.StaffServiceId, Guid.NewGuid(),
            now.AddHours(3), 25m, 30, now);
        var cancelled = new Booking(
            seed.ClientId, seed.StaffId, seed.StaffServiceId, Guid.NewGuid(),
            now.AddHours(4), 25m, 30, now);
        cancelled.Cancel(now.AddHours(1));
        db.Bookings.AddRange(overlapping, nonOverlapping, cancelled);
        await db.SaveChangesAsync();

        var repository = new BookingRepository(db, new SystemClock());
        var overlaps = await repository.GetConfirmedOverlappingAsync(
            seed.StaffId, now.AddHours(1).AddMinutes(15), now.AddHours(1).AddMinutes(45), CancellationToken.None);
        var future = await repository.GetFutureConfirmedAsync(
            seed.StaffId, now.AddHours(2), CancellationToken.None);

        Assert.Equal([overlapping.Id], overlaps.Select(booking => booking.Id));
        Assert.Equal([nonOverlapping.Id], future.Select(booking => booking.Id));
    }

    [Fact]
    public async Task ConcurrentCancellationsReturnThePersistedResult()
    {
        var now = DateTime.UtcNow;
        Guid bookingId;
        Guid clientId;
        await using (var setup = fixture.CreateContext())
        {
            var seed = AddBookingDependencies(setup);
            var booking = new Booking(
                seed.ClientId, seed.StaffId, seed.StaffServiceId, Guid.NewGuid(),
                now.AddHours(1), 25m, 30, now);
            setup.Bookings.Add(booking);
            await setup.SaveChangesAsync();
            bookingId = booking.Id;
            clientId = seed.ClientId;
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var firstTask = new BookingRepository(first, new SystemClock())
            .Cancel(clientId, bookingId, CancellationToken.None);
        var secondTask = new BookingRepository(second, new SystemClock())
            .Cancel(clientId, bookingId, CancellationToken.None);

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.NotNull(results[0]);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(BookingStatus.Cancelled, results[0]!.Status);
    }

    [Fact]
    public async Task ConcurrentCompletionsReturnThePersistedResult()
    {
        var now = DateTime.UtcNow;
        Guid bookingId;
        Guid staffId;
        await using (var setup = fixture.CreateContext())
        {
            var seed = AddBookingDependencies(setup);
            var booking = new Booking(
                seed.ClientId, seed.StaffId, seed.StaffServiceId, Guid.NewGuid(),
                now.AddHours(-2), 25m, 30, now.AddHours(-3));
            setup.Bookings.Add(booking);
            await setup.SaveChangesAsync();
            bookingId = booking.Id;
            staffId = seed.StaffId;
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var firstTask = new BookingRepository(first, new SystemClock())
            .Complete(staffId, bookingId, CancellationToken.None);
        var secondTask = new BookingRepository(second, new SystemClock())
            .Complete(staffId, bookingId, CancellationToken.None);

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.NotNull(results[0]);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(BookingStatus.Completed, results[0]!.Status);
    }

    [Fact]
    public async Task OverlappingRequestsForOneEmployeeCommitExactlyOneBooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var first = seed.Request with { RequestId = Guid.NewGuid() };
        var second = seed.Request with { RequestId = Guid.NewGuid() };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var requests = new[]
        {
            RunAfter(start, () => seed.CreateBookingAsync(first)),
            RunAfter(start, () => seed.CreateBookingAsync(second))
        };
        start.SetResult();

        var results = await Task.WhenAll(requests);

        Assert.Equal(1, results.Count(result => result.Error is null));
        Assert.Equal(1, results.Count(result => result.Error?.Code == ErrorCodes.TimeUnavailable));
        await using var db = fixture.CreateContext();
        fixture.AssertRealPostgres(db);
        Assert.Equal(1, await db.Bookings.CountAsync(b => b.ClientUserId == seed.Client.Id));
    }

    [Fact]
    public async Task DifferentEmployeesAtTheSameTimeBothCommit()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var employee = new User(Interlocked.Increment(ref nextTelegramUserId), "Second employee", null, DateTime.UtcNow);
        var staff = new StaffMember(employee.Id, "Second staff");
        var service = await db.Services.SingleAsync(s => s.Id == (db.StaffServices
            .Where(o => o.Id == seed.Request.StaffServiceId).Select(o => o.ServiceId).Single()));
        var offering = new StaffService(staff.Id, service.Id, 25m, 30);
        db.AddRange(employee, staff, offering, new BookingCoordination(staff),
            new WorkingHours(staff.Id, new TimeOnly(9, 0), new TimeOnly(17, 0)));
        await db.SaveChangesAsync();

        var secondRequest = seed.Request with
        {
            RequestId = Guid.NewGuid(),
            StaffServiceId = offering.Id
        };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new[]
        {
            RunAfter(start, () => seed.CreateBookingAsync(seed.Request with { RequestId = Guid.NewGuid() })),
            RunAfter(start, () => seed.CreateBookingAsync(secondRequest))
        };
        start.SetResult();

        var completed = await Task.WhenAll(results);

        Assert.All(completed, result => Assert.Null(result.Error));
        Assert.Equal(2, await db.Bookings.CountAsync(b => b.ClientUserId == seed.Client.Id));
    }

    [Fact]
    public async Task AdjacentAppointmentsForOneEmployeeBothCommit()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var second = seed.Request with
        {
            RequestId = Guid.NewGuid(),
            StartAtUtc = seed.Request.StartAtUtc.AddMinutes(30)
        };

        var first = await seed.CreateBookingAsync(seed.Request with { RequestId = Guid.NewGuid() });
        var next = await seed.CreateBookingAsync(second);

        Assert.NotEqual(first.Id, next.Id);
        await using var db = fixture.CreateContext();
        Assert.Equal(2, await db.Bookings.CountAsync(b => b.ClientUserId == seed.Client.Id));
    }

    [Fact]
    public async Task CancellationReleasesTheSlotForACommittedRetry()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var created = await seed.CreateBookingAsync(seed.Request);

        await using (var db = fixture.CreateContext())
        {
            var booking = await db.Bookings.SingleAsync(b => b.Id == created.Id);
            booking.Cancel(new DateTime(2026, 11, 9, 8, 30, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        var replacement = await seed.CreateBookingAsync(seed.Request with { RequestId = Guid.NewGuid() });

        Assert.NotEqual(created.Id, replacement.Id);
        await using var verification = fixture.CreateContext();
        Assert.Equal(2, await verification.Bookings.CountAsync(b => b.ClientUserId == seed.Client.Id));
        Assert.Equal(BookingStatus.Cancelled,
            (await verification.Bookings.SingleAsync(b => b.Id == created.Id)).Status);
    }

    private static async Task<Attempt> RunAfter(
        TaskCompletionSource start,
        Func<Task<BookingDto>> operation)
    {
        await start.Task;
        try
        {
            return new Attempt(await operation(), null);
        }
        catch (AppException exception)
        {
            return new Attempt(null, exception);
        }
    }

    private sealed record Attempt(BookingDto? Booking, AppException? Error);

    private static async Task AssertBlockedAsync(Task acquireTask)
    {
        var completed = await Task.WhenAny(acquireTask, Task.Delay(250));
        Assert.NotSame(acquireTask, completed); 
    }

    private static Seed AddBookingDependencies(AppDbContext db)
    {
        var owner = new User(
            Interlocked.Increment(ref nextTelegramUserId),
            "Staff owner",
            null,
            DateTime.UtcNow);
        var client = new User(
            Interlocked.Increment(ref nextTelegramUserId),
            "Client",
            null,
            DateTime.UtcNow);
        var staff = new StaffMember(owner.Id, "Staff");
        var service = new Service("Consultation");
        var staffService = new StaffService(staff.Id, service.Id, 25m, 30);
        db.Users.AddRange(owner, client);
        db.StaffMembers.Add(staff);
        db.Services.Add(service);
        db.StaffServices.Add(staffService);
        return new Seed(client.Id, staff.Id, staffService.Id);
    }

    private sealed record Seed(Guid ClientId, Guid StaffId, Guid StaffServiceId);
}
