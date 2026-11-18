using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

        var conflict = await Assert.ThrowsAsync<DbConflictException>(
            () => ((IUnitOfWork)db).SaveChangesAsync(CancellationToken.None));
        Assert.Equal(ConflictKind.Overlap, conflict.Kind);
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

        var conflict = await Assert.ThrowsAsync<DbConflictException>(
            () => ((IUnitOfWork)db).SaveChangesAsync(CancellationToken.None));
        Assert.Equal(ConflictKind.UniqueRequestId, conflict.Kind);
    }

    [Fact]
    public async Task DirectSqlCannotBypassTheConfirmedOverlapConstraint()
    {
        await using var db = fixture.CreateContext();
        fixture.AssertRealPostgres(db);
        var seed = AddBookingDependencies(db);
        await db.SaveChangesAsync();
        var start = DateTime.UtcNow.AddHours(2);
        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bookings
                    (id, client_user_id, staff_member_id, staff_service_id, request_id,
                     starts_at_utc, ends_at_utc, price, duration_minutes, status, created_at_utc)
                VALUES
                    ({Guid.NewGuid()}, {seed.ClientId}, {seed.StaffId}, {seed.StaffServiceId},
                     {Guid.NewGuid()}, {start.AddMinutes(15)}, {start.AddMinutes(45)},
                     {25m}, {30}, {"Confirmed"}, {DateTime.UtcNow})
                """));

        Assert.Equal("ex_bookings_staff_confirmed_overlap", exception.ConstraintName);
        await using var verification = fixture.CreateContext();
        Assert.Equal(1, await verification.Bookings.CountAsync(b => b.StaffMemberId == seed.StaffId));
    }

    [Fact]
    public async Task DirectSqlCannotBypassTheRequestIdUniquenessConstraint()
    {
        await using var db = fixture.CreateContext();
        var seed = AddBookingDependencies(db);
        await db.SaveChangesAsync();
        var start = DateTime.UtcNow.AddHours(2);
        var requestId = Guid.NewGuid();
        db.Bookings.Add(CreateBooking(seed, seed.ClientId, start, requestId));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bookings
                    (id, client_user_id, staff_member_id, staff_service_id, request_id,
                     starts_at_utc, ends_at_utc, price, duration_minutes, status, created_at_utc)
                VALUES
                    ({Guid.NewGuid()}, {seed.ClientId}, {seed.StaffId}, {seed.StaffServiceId},
                     {requestId}, {start.AddHours(2)}, {start.AddHours(2).AddMinutes(30)},
                     {25m}, {30}, {"Confirmed"}, {DateTime.UtcNow})
                """));

        Assert.Equal("ux_bookings_client_request", exception.ConstraintName);
        await using var verification = fixture.CreateContext();
        Assert.Single(await verification.Bookings.Where(b => b.RequestId == requestId).ToListAsync());
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
