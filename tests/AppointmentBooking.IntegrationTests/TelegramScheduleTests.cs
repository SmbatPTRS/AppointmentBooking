using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using User = AppointmentBooking.Domain.Entities.User;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TelegramScheduleTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EmployeeEditsHoursWithBreakAndCanRemoveBreak()
    {
        await using var session = await Open();
        await session.Text("/schedule");
        await session.Click("Edit hours");
        await session.Text("09:00 18:00 13:00 14:00");
        await using var db = fixture.CreateContext();
        var saved = await db.WorkingHours.AsNoTracking().SingleAsync(h => h.StaffMemberId == session.Staff.Id);
        Assert.Equal(new TimeOnly(9, 0), saved.OpensAt);
        Assert.Equal(new TimeOnly(18, 0), saved.ClosesAt);
        Assert.Equal(new TimeOnly(13, 0), saved.BreakStartsAt);
        Assert.Equal(new TimeOnly(14, 0), saved.BreakEndsAt);
        await session.Click("Edit hours");
        await session.Text("00:00 18:00");
        saved = await db.WorkingHours.AsNoTracking().SingleAsync(h => h.StaffMemberId == session.Staff.Id);
        Assert.Equal(TimeOnly.MinValue, saved.OpensAt);
        Assert.Null(saved.BreakStartsAt);
        Assert.Null(saved.BreakEndsAt);
    }

    [Theory]
    [InlineData("9:00 18:00")]
    [InlineData("18:00 09:00")]
    [InlineData("09:00 18:00 08:00 10:00")]
    [InlineData("09:00 18:00 13:00")]
    public async Task InvalidHoursAllowCorrectionWithoutSaving(string input)
    {
        await using var session = await Open();
        await session.Text("/schedule");
        await session.Click("Edit hours");
        await session.Text(input);
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == session.Staff.Id));
        await session.Text("10:00 17:00");
        Assert.Equal(new TimeOnly(10, 0), (await db.WorkingHours.SingleAsync(h => h.StaffMemberId == session.Staff.Id)).OpensAt);
    }

    [Fact]
    public async Task DaysOffSupportDateWeekdayDuplicateRejectionAndRemoval()
    {
        await using var session = await Open();
        await session.Text("/schedule");
        await session.Click("Add day off");
        await session.Text("2027-02-30");
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == session.Staff.Id));
        await session.Text("2027-02-28");
        await session.Click("Add day off");
        await session.Text("Sunday");
        await session.Click("Add day off");
        await session.Text("Sunday");
        Assert.Equal(2, await db.StaffDaysOff.CountAsync(d => d.StaffMemberId == session.Staff.Id));
        await session.Click("Back");
        await session.Click("Remove 2027-02-28");
        var day = await db.StaffDaysOff.SingleAsync(d => d.StaffMemberId == session.Staff.Id);
        Assert.Equal(DayOfWeek.Sunday, day.Weekday);
    }

    [Fact]
    public async Task DeactivationAfterOpeningFormDeniesSave()
    {
        await using var session = await Open();
        await session.Text("/schedule");
        await session.Click("Edit hours");
        await using var db = fixture.CreateContext();
        var staff = await db.StaffMembers.SingleAsync(s => s.Id == session.Staff.Id);
        staff.Deactivate();
        await db.SaveChangesAsync();
        await session.Text("09:00 18:00");
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == staff.Id));
        Assert.Contains("permission", session.Sender.SentMessages.Last().Text);
    }

    [Fact]
    public async Task OwnerSelectsAnotherEmployeeButCustomerCannotOpenSchedule()
    {
        await using var owner = await Open(owner: true);
        await owner.Text("/schedule");
        while (!owner.Sender.SentMenus.Last().Buttons.Any(b => b.Text == owner.Staff.DisplayName))
            await owner.Click("Next employees");
        await owner.Click(owner.Staff.DisplayName);
        await owner.Click("Edit hours");
        await owner.Text("08:00 16:00");
        await using var db = fixture.CreateContext();
        Assert.True(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == owner.Staff.Id));
        await using var customer = await Open(customer: true);
        await customer.Text("/schedule");
        Assert.Contains("permission", customer.Sender.SentMessages.Last().Text);
        Assert.Empty(customer.Sender.SentMenus);
    }

    [Fact]
    public async Task BackCancelAndOrdinaryTextDoNotWriteOrReplaceOverviewButtons()
    {
        await using var session = await Open();
        await session.Text("/schedule");
        var edit = session.Button("Edit hours");
        var count = session.Sender.SentMessages.Count;
        await session.Text("hello");
        Assert.Equal(count, session.Sender.SentMessages.Count);
        await session.Callback(edit);
        await session.Click("Back");
        await session.Click("Add day off");
        await session.Click("Cancel");
        await session.Text("2027-02-28");
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == session.Staff.Id));
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == session.Staff.Id));
    }

    [Fact]
    public async Task FailedReplyAfterDayOffCommitDoesNotRepeatMutation()
    {
        await using var session = await Open();
        await session.Text("/schedule");
        await session.Click("Add day off");
        var update = session.Message("2027-03-01");
        session.Sender.FailNextSend(new IOException("Telegram unavailable"));
        await Assert.ThrowsAsync<IOException>(() => session.Dispatch(update));
        await using var db = fixture.CreateContext();
        var id = (await db.StaffDaysOff.SingleAsync(d => d.StaffMemberId == session.Staff.Id)).Id;
        await session.Dispatch(update);
        Assert.Equal(id, (await db.StaffDaysOff.SingleAsync(d => d.StaffMemberId == session.Staff.Id)).Id);
        Assert.Contains(session.Sender.SentMenus.Last().Buttons, b => b.Text == "Remove 2027-03-01");
    }

    [Fact]
    public async Task FutureBookingRejectsShortenedHoursAndDayOffWithoutChangingSchedule()
    {
        await using var session = await Open();
        await session.Text("/schedule");
        await session.Click("Edit hours");
        await session.Text("09:00 18:00");
        await using var db = fixture.CreateContext();
        var settings = await db.BusinessSettings.SingleAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)).AddDays(2);
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(10, 0)), zone);
        var service = new Service("Schedule conflict");
        var offering = new StaffService(session.Staff.Id, service.Id, 100m, 30);
        db.AddRange(service, offering, new Booking(session.Staff.UserId, session.Staff.Id, offering.Id,
            Guid.NewGuid(), start, 100m, 30, DateTime.UtcNow));
        await db.SaveChangesAsync();
        await session.Click("Edit hours");
        await session.Text("11:00 18:00");
        Assert.Contains("conflicts", session.Sender.SentMessages.Last().Text);
        Assert.Equal(new TimeOnly(9, 0), (await db.WorkingHours.SingleAsync(h => h.StaffMemberId == session.Staff.Id)).OpensAt);
        await session.Click("Back");
        await session.Click("Add day off");
        await session.Text(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("conflicts", session.Sender.SentMessages.Last().Text);
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == session.Staff.Id));
    }

    [Fact]
    public async Task PaginatedDaysOffKeepAllItemsReachableAndRejectOldButtons()
    {
        await using var session = await Open();
        await using var db = fixture.CreateContext();
        db.StaffDaysOff.AddRange(Enumerable.Range(1, 12).Select(day =>
            new StaffDayOff(session.Staff.Id, null, new DateOnly(2027, 4, day))));
        await db.SaveChangesAsync();
        await session.Text("/schedule");
        var oldRemove = session.Button("Remove 2027-04-01");
        await session.Click("Next days off");
        await session.Callback(oldRemove);
        Assert.Equal(12, await db.StaffDaysOff.CountAsync(d => d.StaffMemberId == session.Staff.Id));
        Assert.Contains("expired", session.Sender.CallbackAnswers.Last().Text!);
        await session.Click("Remove 2027-04-12");
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == session.Staff.Id && d.Date == new DateOnly(2027, 4, 12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryingCommittedHoursOrRemovalOnlyRendersCurrentSchedule(bool remove)
    {
        await using var session = await Open();
        await using var db = fixture.CreateContext();
        var day = new StaffDayOff(session.Staff.Id, null, new DateOnly(2027, 5, 1));
        db.StaffDaysOff.Add(day);
        await db.SaveChangesAsync();
        await session.Text("/schedule");
        Update update;
        if (remove) update = session.CallbackUpdate(session.Button("Remove 2027-05-01"));
        else
        {
            await session.Click("Edit hours");
            update = session.Message("09:00 18:00");
        }
        session.Sender.FailNextSend(new IOException("Send failed"));
        await Assert.ThrowsAsync<IOException>(() => session.Dispatch(update));
        // Another operation changes the database before the original delivery is retried.
        // Repeating SetHours would overwrite it; repeating removal would return NotFound.
        if (!remove)
        {
            var hours = await db.WorkingHours.SingleAsync(h => h.StaffMemberId == session.Staff.Id);
            hours.Update(new TimeOnly(10, 0), new TimeOnly(17, 0), null, null);
            await db.SaveChangesAsync();
        }
        await session.Dispatch(update);
        if (remove) Assert.DoesNotContain(session.Sender.SentMenus.Last().Buttons, b => b.Text == "Remove 2027-05-01");
        else
        {
            Assert.Contains("10:00", session.Sender.SentMenus.Last().Text);
            Assert.Equal(new TimeOnly(10, 0), (await db.WorkingHours.AsNoTracking().SingleAsync(h => h.StaffMemberId == session.Staff.Id)).OpensAt);
        }
    }

    [Fact]
    public async Task RemovalRetriesWhenFailureHappenedBeforeCommit()
    {
        var failures = new Failures();
        await using var session = await Open(failures: failures);
        await using var db = fixture.CreateContext();
        var day = new StaffDayOff(session.Staff.Id, null, new DateOnly(2027, 6, 1));
        db.StaffDaysOff.Add(day);
        await db.SaveChangesAsync();
        await session.Text("/schedule");
        var update = session.CallbackUpdate(session.Button("Remove 2027-06-01"));
        failures.Lock = true;
        await Assert.ThrowsAsync<IOException>(() => session.Dispatch(update));
        Assert.True(await db.StaffDaysOff.AnyAsync(d => d.Id == day.Id));
        await session.Dispatch(update);
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.Id == day.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryRetryAfterAuthorizationFailureLeavesOldForm(bool owner)
    {
        var failures = new Failures();
        await using var session = await Open(owner: owner, failures: failures);
        await session.Text("/schedule");
        if (owner)
        {
            while (!session.Sender.SentMenus.Last().Buttons.Any(b => b.Text == session.Staff.DisplayName))
                await session.Click("Next employees");
            await session.Click(session.Staff.DisplayName);
        }
        await session.Click("Edit hours");
        var entry = session.Message("/schedule");
        failures.Access = true;
        await Assert.ThrowsAsync<IOException>(() => session.Dispatch(entry));
        await session.Dispatch(entry);
        await session.Text("09:00 18:00");
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == session.Staff.Id));
        await session.Click("Back");
        Assert.Contains(session.Sender.SentMenus.Last().Buttons, b => b.Text == "Book an appointment");
    }

    // Fault injection preserves the actual authorization and PostgreSQL locking behavior.
    private sealed class Failures { public bool Lock; public bool Access; }
    private sealed class FailingLock(PostgresStaffOperationLock inner, Failures failures) : IStaffOperationLock
    {
        public Task AcquireAsync(Guid id, CancellationToken ct)
        {
            if (failures.Lock) { failures.Lock = false; throw new IOException("Before lock"); }
            return inner.AcquireAsync(id, ct);
        }
    }
    private sealed class FailingAccess(IAccessService inner, Failures failures) : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) =>
            inner.IsOwnerAsync(actor, ct);


        public Task RequireOwnerAsync(Actor actor, CancellationToken ct)
        {
            if (failures.Access) { failures.Access = false; throw new IOException("Before authorization"); }
            return inner.RequireOwnerAsync(actor, ct);
        }
        public Task RequireStaffManagementAsync(Actor actor, Guid id, CancellationToken ct) => inner.RequireStaffManagementAsync(actor, id, ct);
        public Task RequireAppointmentManagementAsync(Actor actor, Guid id, CancellationToken ct) => inner.RequireAppointmentManagementAsync(actor, id, ct);
    }

    private async Task<Session> Open(bool owner = false, bool customer = false, Failures? failures = null)
    {
        await using var db = fixture.CreateContext();
        var telegramId = Random.Shared.NextInt64(10000000000, 90000000000);
        var employee = new User(telegramId + 1, "Schedule employee", null, DateTime.UtcNow);
        var caller = new User(telegramId, "Schedule caller", null, DateTime.UtcNow);
        var staff = new StaffMember(owner || customer ? employee.Id : caller.Id, "Schedule-" + Guid.NewGuid());
        db.AddRange(employee, caller, staff);
        if (!await db.BusinessSettings.AnyAsync())
            db.Add(new BusinessSettings("Studio", "Staff", "Asia/Yerevan", "AMD"));
        await db.SaveChangesAsync();
        var sender = new FakeTelegramClient();
        var root = new ApiFactory(fixture.ConnectionString);
        var app = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITelegramMessageSender>(sender);
            if (failures is not null)
            {
                var accessFactory = services.Last(d => d.ServiceType == typeof(IAccessService)).ImplementationFactory!;
                services.AddScoped<IAccessService>(provider => new FailingAccess((IAccessService)accessFactory(provider), failures));
                services.AddScoped<IStaffOperationLock>(provider => new FailingLock(
                    new PostgresStaffOperationLock(provider.GetRequiredService<AppDbContext>()), failures));
            }
            services.Configure<OwnerOptions>(options => options.TelegramUserId = owner ? telegramId : 0);
        }));
        return new Session(root, app, sender, telegramId, staff);
    }

    private sealed class Session(ApiFactory root, WebApplicationFactory<Program> app,
        FakeTelegramClient sender, long telegramId, StaffMember staff) : IAsyncDisposable
    {
        private int updateId;
        public FakeTelegramClient Sender => sender;
        public StaffMember Staff => staff;
        public string Button(string label) => Assert.Single(sender.SentMenus.Last().Buttons, b => b.Text == label).CallbackData;
        public Update Message(string text) => new() { Id = ++updateId, Message = new Message {
            From = new Telegram.Bot.Types.User { Id = telegramId, FirstName = "Schedule" },
            Chat = new Chat { Id = telegramId }, Text = text } };
        public Task Text(string text) => Dispatch(Message(text));
        public Task Click(string label) => Callback(Button(label));
        public Task Callback(string data) => Dispatch(CallbackUpdate(data));
        public Update CallbackUpdate(string data) => new Update { Id = ++updateId, CallbackQuery = new CallbackQuery {
            Id = Guid.NewGuid().ToString(), From = new Telegram.Bot.Types.User { Id = telegramId, FirstName = "Schedule" },
            ChatInstance = "schedule", Data = data, Message = new Message { Chat = new Chat { Id = telegramId } } } };
        public async Task Dispatch(Update update)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
        }
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); await root.DisposeAsync(); }
    }
}
