using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReadsOnlySelectedEmployeesHoursAndMatchingDaysOff()
    {
        await using var db = fixture.CreateContext();
        var first = new User(4300000011, "First", null, DateTime.UtcNow);
        var second = new User(4300000012, "Second", null, DateTime.UtcNow);
        var staff = new StaffMember(first.Id, "Aram");
        var other = new StaffMember(second.Id, "David");
        var date = new DateOnly(2026, 11, 8);
        db.Users.AddRange(first, second);
        db.StaffMembers.AddRange(staff, other);
        db.WorkingHours.Add(new WorkingHours(staff.Id, new(9, 0), new(18, 0), new(13, 0), new(14, 0)));
        db.StaffDaysOff.AddRange(
            new StaffDayOff(staff.Id, date.DayOfWeek, null),
            new StaffDayOff(staff.Id, null, date),
            new StaffDayOff(staff.Id, null, date.AddDays(5)),
            new StaffDayOff(other.Id, null, date));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var repo = new ScheduleRepository(db);
        var ct = CancellationToken.None;
        Assert.Equal(new WorkingHoursModel(new(9, 0), new(18, 0), new(13, 0), new(14, 0)),
            await repo.GetHoursAsync(staff.Id, ct));
        Assert.Null(await repo.GetHoursAsync(other.Id, ct));

        var days = await repo.GetDaysOffForDateAsync(staff.Id, date, ct);
        Assert.Equal(2, days.Count);
        Assert.Contains(new DayOffModel(Weekday: date.DayOfWeek), days);
        Assert.Contains(new DayOffModel(Date: date), days);
        Assert.Empty(await repo.GetDaysOffForDateAsync(staff.Id, date.AddDays(1), ct));
        Assert.Equal(new DayOffModel(Date: date),
            Assert.Single(await repo.GetDaysOffForDateAsync(other.Id, date, ct)));
    }
}
