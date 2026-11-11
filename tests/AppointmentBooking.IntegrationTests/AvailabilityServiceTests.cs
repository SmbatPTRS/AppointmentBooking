using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AvailabilityServiceTests(PostgresFixture fixture)
{
    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task ConfirmedBookingRemovesOverlapsAndCancellationRestoresSlots()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var clock = new Clock();
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null)
            db.BusinessSettings.Add(new BusinessSettings("Studio", "Staff", "Asia/Yerevan", "AMD"));
        else
            settings.Update("Studio", "Staff", "Asia/Yerevan", "AMD");

        var user = new User(4300000021, "Aram", null, clock.UtcNow);
        var staff = new StaffMember(user.Id, "Aram");
        var service = new Service("Haircut");
        var offering = new StaffService(staff.Id, service.Id, 5000m, 30);
        db.AddRange(user, staff, service, offering, new WorkingHours(staff.Id, new(9, 0), new(11, 0)));
        await db.SaveChangesAsync();
        var availability = new AvailabilityService(new CatalogRepository(db), new StaffRepository(db),
            new ScheduleRepository(db), new BusinessSettingsRepository(db), new BookingRepository(db, clock),
            clock, new AvailabilityCalculator(new BookingOptions()));
        Task<IReadOnlyList<AvailableSlot>> List() =>
            availability.ListAsync(staff.Id, offering.Id, new(2026, 11, 4), CancellationToken.None);

        Task<bool> Check(DateTimeOffset requestedStart) =>
            availability.IsAvailableAsync(staff.Id, offering.Id, new(2026, 11, 4), requestedStart, CancellationToken.None);

        var before = await List();
        var start = clock.UtcNow.AddHours(2); // 10:00 in Yerevan
        Assert.Contains(before, s => s.StartsAtUtc == start);
        Assert.True(await Check(new DateTimeOffset(start).ToOffset(TimeSpan.FromHours(4))));
        Assert.False(await Check(start.AddMinutes(1)));
        var booking = new Booking(user.Id, staff.Id, offering.Id, Guid.NewGuid(), start, 5000m, 30, clock.UtcNow);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var during = await List();
        Assert.Equal(before.Where(s => s.EndsAtUtc <= start || s.StartsAtUtc >= start.AddMinutes(30)), during);
        Assert.Contains(during, s => s.StartsAtUtc == start.AddMinutes(30));
        Assert.False(await Check(start));
        Assert.True(await Check(start.AddMinutes(30)));
        booking.Cancel(clock.UtcNow);
        await db.SaveChangesAsync();
        Assert.True(await Check(start));
        Assert.Equal(before.ToArray(), (await List()).ToArray());
    }
}
