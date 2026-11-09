using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleStorageTests(PostgresFixture fixture)
{
    private static long nextTelegramId = 4300000100;
    private static StaffMember AddStaff(AppDbContext db)
    {
        var user = new User(Interlocked.Increment(ref nextTelegramId), "Employee", null, DateTime.UtcNow);
        var staff = new StaffMember(user.Id, "Employee");
        db.Users.Add(user);
        db.StaffMembers.Add(staff);
        return staff;
    }

    [Fact]
    public async Task HoursAreStagedAndTrackedUpdatesPersistOnlyWhenCallerSaves()
    {
        await using var db = fixture.CreateContext();
        var staff = AddStaff(db);
        await db.SaveChangesAsync();
        var repo = new ScheduleRepository(db);
        var ct = CancellationToken.None;
        Assert.Null(await repo.GetWorkingHoursAsync(staff.Id, ct));
        var hours = new WorkingHours(staff.Id, new(9, 0), new(18, 0));
        repo.Add(hours);
        await using var observer = fixture.CreateContext();
        var reader = new ScheduleRepository(observer);
        Assert.Null(await reader.GetHoursAsync(staff.Id, ct));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var tracked = Assert.IsType<WorkingHours>(await repo.GetWorkingHoursAsync(staff.Id, ct));
        var id = tracked.Id;
        tracked.Update(new(10, 0), new(19, 0), new(13, 0), new(14, 0));
        Assert.Equal(new TimeOnly(9, 0), (await reader.GetHoursAsync(staff.Id, ct))!.OpensAt);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var saved = Assert.IsType<WorkingHours>(await repo.GetWorkingHoursAsync(staff.Id, ct));
        Assert.Equal(id, saved.Id);
        Assert.Equal(new TimeOnly(10, 0), saved.OpensAt);
        Assert.Equal(new TimeOnly(19, 0), saved.ClosesAt);
        Assert.Equal(new TimeOnly(13, 0), saved.BreakStartsAt);
        Assert.Equal(new TimeOnly(14, 0), saved.BreakEndsAt);
        saved.Update(new(9, 0), new(18, 0), null, null);
        await db.SaveChangesAsync();
        var noBreak = await reader.GetHoursAsync(staff.Id, ct);
        Assert.Null(noBreak!.BreakStartsAt);
        Assert.Null(noBreak.BreakEndsAt);
    }

    [Fact]
    public async Task DaysOffAreScopedByEmployeeAndRemovalIsStaged()
    {
        await using var db = fixture.CreateContext();
        var staff = AddStaff(db);
        var other = AddStaff(db);
        await db.SaveChangesAsync();
        var repo = new ScheduleRepository(db);
        var ct = CancellationToken.None;
        var sunday = new StaffDayOff(staff.Id, DayOfWeek.Sunday, null);
        var dated = new StaffDayOff(staff.Id, null, new(2027, 1, 3));
        var otherDay = new StaffDayOff(other.Id, null, new(2027, 1, 3));
        repo.Add(sunday);
        repo.Add(dated);
        repo.Add(otherDay);
        await using var observer = fixture.CreateContext();
        var reader = new ScheduleRepository(observer);
        Assert.Empty(await reader.ListDaysOffAsync(staff.Id, ct));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var days = await repo.ListDaysOffAsync(staff.Id, ct);
        Assert.Equal(2, days.Count);
        Assert.All(days, day => Assert.Equal(staff.Id, day.StaffMemberId));
        Assert.Null(await repo.GetDayOffAsync(staff.Id, otherDay.Id, ct));
        Assert.Null(await repo.GetDayOffAsync(staff.Id, Guid.NewGuid(), ct));
        var removable = Assert.IsType<StaffDayOff>(await repo.GetDayOffAsync(staff.Id, dated.Id, ct));
        repo.Remove(removable);
        Assert.NotNull(await reader.GetDayOffAsync(staff.Id, dated.Id, ct));
        await db.SaveChangesAsync();
        observer.ChangeTracker.Clear();
        Assert.Null(await reader.GetDayOffAsync(staff.Id, dated.Id, ct));
        Assert.Equal(sunday.Id, Assert.Single(await reader.ListDaysOffAsync(staff.Id, ct)).Id);
        Assert.Equal(otherDay.Id, Assert.Single(await reader.ListDaysOffAsync(other.Id, ct)).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseRejectsDuplicateDayOffRulesForOneEmployee(bool recurring)
    {
        await using var db = fixture.CreateContext();
        var staff = AddStaff(db);
        await db.SaveChangesAsync();
        var repo = new ScheduleRepository(db);
        DayOfWeek? weekday = recurring ? DayOfWeek.Sunday : null;
        DateOnly? date = recurring ? null : new DateOnly(2027, 1, 3);
        repo.Add(new StaffDayOff(staff.Id, weekday, date));
        await db.SaveChangesAsync();
        repo.Add(new StaffDayOff(staff.Id, weekday, date));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseRejectsSecondScheduleForOneEmployee()
    {
        await using var db = fixture.CreateContext();
        var staff = AddStaff(db);
        await db.SaveChangesAsync();
        var repo = new ScheduleRepository(db);
        repo.Add(new WorkingHours(staff.Id, new(9, 0), new(18, 0)));
        await db.SaveChangesAsync();
        await using var competing = fixture.CreateContext();
        new ScheduleRepository(competing).Add(new WorkingHours(staff.Id, new(10, 0), new(19, 0)));
        await Assert.ThrowsAsync<DbUpdateException>(() => competing.SaveChangesAsync());
    }
}
