using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Time;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

public sealed class BusinessSettingsServiceTests
{
    [Fact]
    public async Task OwnerCanChangeTimeZoneAndCurrencyOnlyBeforeAnyBooking()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        await new DatabaseSeeder(db).SeedBusinessSettingsAsync(
            new BusinessSettings("Business", "Employee", "UTC", "AMD"));

        var now = DateTime.UtcNow;
        var user = new User(8_000_000_004, "Owner", null, now);
        var staff = new StaffMember(user.Id, "Staff");
        var service = new Service("Consultation");
        var offering = new StaffService(staff.Id, service.Id, 25m, 30);
        db.AddRange(user, staff, service, offering);
        await db.SaveChangesAsync();

        var actor = new Actor(user.Id);
        var settings = new BusinessSettingsService(new OwnerAccess(actor),
            new BusinessSettingsRepository(db), new BookingRepository(db, new SystemClock()),
            new BookingCoordinationRepository(db), db);

        var invalid = await Assert.ThrowsAsync<AppException>(
            () => settings.UpdateTimeZoneAndCurrencyAsync(actor, "Invalid/Zone", "USD"));
        Assert.Equal(ErrorCodes.InvalidTimeZone, invalid.Code);
        Assert.Equal("AMD", (await settings.GetAsync()).CurrencyCode);
        Assert.Equal(1L, await db.BookingCoordinationRows.Select(row => row.ChangeVersion).SingleAsync());

        var updated = await settings.UpdateTimeZoneAndCurrencyAsync(actor, "Asia/Yerevan", "usd");
        Assert.Equal("Asia/Yerevan", updated.TimeZoneId);
        Assert.Equal("USD", updated.CurrencyCode);

        var booking = new Booking(user.Id, staff.Id, offering.Id, Guid.NewGuid(),
            now.AddHours(1), 25m, 30, now);
        booking.Cancel(now);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var locked = await Assert.ThrowsAsync<AppException>(
            () => settings.UpdateTimeZoneAndCurrencyAsync(actor, "UTC", "AMD"));
        Assert.Equal(ErrorCodes.BusinessSettingsLocked, locked.Code);

        var labels = await settings.UpdateLabelsAsync(actor, "New name", "Specialist");
        Assert.Equal(new BusinessSettingsView("New name", "Specialist", "Asia/Yerevan", "USD"), labels);
        Assert.Equal(3L, await db.BookingCoordinationRows.Select(row => row.ChangeVersion).SingleAsync());
    }

    private sealed class OwnerAccess(Actor owner) : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) =>
            Task.FromResult(actor == owner);

        public Task RequireOwnerAsync(Actor actor, CancellationToken ct)
        {
            Assert.Equal(owner, actor);
            return Task.CompletedTask;
        }

        public Task RequireStaffManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RequireAppointmentManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
