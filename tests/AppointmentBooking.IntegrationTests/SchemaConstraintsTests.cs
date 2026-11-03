using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SchemaConstraintsTests(PostgresFixture fixture)
{
    private static long nextTelegramUserId = 1000000000;

    [Fact]
    public async Task ConfirmedBookingsForTheSameStaffCannotOverlap()
    {
        await using var db = fixture.CreateContext();
        var seed = AddBookingDependencies(db);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddHours(2);
        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start));
        await db.SaveChangesAsync();

        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start.AddMinutes(15)));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CancelledBookingsDoNotParticipateInOverlapConstraint()
    {
        await using var db = fixture.CreateContext();
        var seed = AddBookingDependencies(db);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddHours(2);
        var cancelled = CreateBooking(seed, seed.ClientId, start);
        cancelled.Cancel(start.AddMinutes(-30));
        db.Bookings.Add(cancelled);
        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start.AddMinutes(15)));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AClientRequestIdIsUnique()
    {
        await using var db = fixture.CreateContext();
        var seed = AddBookingDependencies(db);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddHours(2);
        var requestId = Guid.NewGuid();
        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start, requestId));
        await db.SaveChangesAsync();

        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start.AddHours(2), requestId));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync());
    }

    private static Booking CreateBooking(
        Seed seed,
        Guid clientId,
        DateTime start,
        Guid? requestId = null)
    {
        return new Booking(
            clientId,
            seed.StaffId,
            seed.StaffServiceId,
            requestId ?? Guid.NewGuid(),
            start,
            25m,
            30,
            start.AddMinutes(-30));
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
