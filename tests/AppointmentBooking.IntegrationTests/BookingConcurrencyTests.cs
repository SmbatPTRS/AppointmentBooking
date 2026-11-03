using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
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

        var repository = new BookingRepository(db);
        var overlaps = await repository.GetConfirmedOverlappingAsync(
            seed.StaffId, now.AddHours(1).AddMinutes(15), now.AddHours(1).AddMinutes(45), CancellationToken.None);
        var future = await repository.GetFutureConfirmedAsync(
            seed.StaffId, now.AddHours(2), CancellationToken.None);

        Assert.Equal([overlapping.Id], overlaps.Select(booking => booking.Id));
        Assert.Equal([nonOverlapping.Id], future.Select(booking => booking.Id));
    }

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
