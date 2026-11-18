using System.Net;
using System.Net.Http.Json;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleDeliveryChannelTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RetriedHoursReplyCannotOverwriteANewerHttpSchedule()
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await scenario.Send("/schedule");
        await scenario.Click("Edit hours");
        var delivery = scenario.Message("09:00 18:00");
        await FailReply(scenario, delivery);
        Assert.Equal(new WorkingHoursModel(new(9, 0), new(18, 0)),
            await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        var firstSlots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.Equal(scenario.At(new(9, 0)), firstSlots[0].StartsAtUtc);
        var newer = new WorkingHoursModel(new(10, 0), new(14, 0), new(11, 0), new(12, 0));
        using var updated = await employee.PutAsJsonAsync(scenario.ScheduleUrl, newer);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var expectedSlots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(expectedSlots);
        await FailReply(scenario, delivery);
        Assert.Equal(newer, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Equal(expectedSlots, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        var sent = scenario.Sender.SentMenus.Count;
        await scenario.Deliver(delivery);
        Assert.Equal(sent + 1, scenario.Sender.SentMenus.Count);
        Assert.Contains("Hours: 10:00–14:00", scenario.LastMenu);
        Assert.Contains("Break: 11:00–12:00", scenario.LastMenu);
        Assert.Equal(newer, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        await using var db = fixture.CreateContext();
        Assert.Single(await db.WorkingHours.Where(h => h.StaffMemberId == scenario.Staff.Id).ToListAsync());
        var buttons = scenario.Sender.SentMenus.Last().Buttons.ToArray();
        await scenario.Send("ordinary message");
        Assert.Equal(sent + 1, scenario.Sender.SentMenus.Count);
        Assert.Equal(buttons, scenario.Sender.SentMenus.Last().Buttons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriedDayOffAdditionDoesNotRecreateAnHttpDeletedRow(bool weekly)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(before);
        await scenario.Send("/schedule");
        await scenario.Click("Add day off");
        var delivery = scenario.Message(weekly ? "Tuesday" : "2026-11-10");
        await FailReply(scenario, delivery);
        var committed = Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        using var removed = await employee.DeleteAsync($"{scenario.DaysUrl}/{committed.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await FailReply(scenario, delivery);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        await scenario.Deliver(delivery);
        Assert.Contains("Days off: 0", scenario.LastMenu);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.Id == committed.Id));
        // A new user action must still work after the old delivery has recovered.
        await scenario.Click("Add day off");
        await scenario.Send(weekly ? "Tuesday" : "2026-11-10");
        var replacement = Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.NotEqual(committed.Id, replacement.Id);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriedRemovalShowsTheHttpReplacementWithoutDeletingIt(bool weekly)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(before);
        var model = weekly ? new DayOffModel(Weekday: DayOfWeek.Tuesday) : new DayOffModel(Date: scenario.Date);
        var label = weekly ? "Remove Tuesday" : "Remove 2026-11-10";
        using var added = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var original = (await added.Content.ReadFromJsonAsync<DayOffDto>())!;
        await scenario.Send("/schedule");
        var delivery = scenario.CallbackUpdate(scenario.Sender.SentMenus.Last().Buttons.Single(b => b.Text == label).CallbackData);
        await FailReply(scenario, delivery);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        using var recreated = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
        var replacement = (await recreated.Content.ReadFromJsonAsync<DayOffDto>())!;
        Assert.NotEqual(original.Id, replacement.Id);
        await FailReply(scenario, delivery);
        Assert.Equal(replacement, Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!));
        await scenario.Deliver(delivery);
        Assert.Contains("Days off: 1", scenario.LastMenu);
        Assert.Contains(scenario.Sender.SentMenus.Last().Buttons, b => b.Text == label);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        Assert.Equal(replacement, Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!));
        // The restored button must bind the replacement ID, not the deleted original.
        await scenario.Click(label);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
    }

    [Fact]
    public async Task FailedValidationReplyKeepsTheFormUsableAndDoesNotUndoHttpChanges()
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        await scenario.Send("/schedule");
        await scenario.Click("Edit hours");
        var invalid = scenario.Message("18:00 09:00");
        await FailReply(scenario, invalid);
        var newer = new WorkingHoursModel(new(10, 0), new(15, 0));
        using var changed = await employee.PutAsJsonAsync(scenario.ScheduleUrl, newer);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var expectedSlots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        await scenario.Deliver(invalid);
        Assert.Contains("Send opening and closing times", scenario.LastMenu);
        Assert.Equal(newer, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Equal(expectedSlots, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await scenario.Send("11:00 16:00");
        Assert.Equal(new WorkingHoursModel(new(11, 0), new(16, 0)),
            await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        var finalSlots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.Equal(scenario.At(new(11, 0)), finalSlots[0].StartsAtUtc);
        Assert.Equal(scenario.At(new(16, 0)), finalSlots[^1].EndsAtUtc);
    }

    [Fact]
    public async Task FailedBackReplyRestoresOnlyOneScreenAndLeavesHttpStateIntact()
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await scenario.Send("/schedule");
        await scenario.Click("Edit hours");
        await scenario.Send("09:00 12:00");
        var hours = await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl);
        var slots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(slots);
        var back = scenario.CallbackUpdate(scenario.Sender.SentMenus.Last().Buttons.Single(b => b.Text == "Back").CallbackData);
        await FailReply(scenario, back);
        await scenario.Deliver(back);
        Assert.Contains("Hours: 09:00–12:00", scenario.LastMenu);
        Assert.Equal(hours, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Equal(slots, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await scenario.Click("Back");
        Assert.Contains(scenario.Sender.SentMenus.Last().Buttons, b => b.Text == "Book an appointment");
        Assert.Equal(hours, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
    }

    [Fact]
    public async Task ReplyRetryRechecksAccessAfterEmployeeDeactivation()
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await scenario.Send("/schedule");
        await scenario.Click("Edit hours");
        var delivery = scenario.Message("09:00 12:00");
        await FailReply(scenario, delivery);
        var newer = new WorkingHoursModel(new(10, 0), new(17, 0));
        using var updated = await employee.PutAsJsonAsync(scenario.ScheduleUrl, newer);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == scenario.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var menus = scenario.Sender.SentMenus.Count;
        await scenario.Deliver(delivery);
        Assert.Equal(menus, scenario.Sender.SentMenus.Count);
        Assert.Contains("permission", scenario.Sender.SentMessages.Last().Text);
        using var denied = await employee.GetAsync(scenario.ScheduleUrl);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await using var verify = fixture.CreateContext();
        var saved = await verify.WorkingHours.SingleAsync(h => h.StaffMemberId == scenario.Staff.Id);
        Assert.Equal(newer.OpensAt, saved.OpensAt);
        Assert.Equal(newer.ClosesAt, saved.ClosesAt);
    }

    private static async Task ConfigureHours(HttpClient employee, ScheduleChannelScenario scenario)
    {
        using var configured = await employee.PutAsJsonAsync(scenario.ScheduleUrl, new WorkingHoursModel(new(9, 0), new(12, 0)));
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
    }

    private static async Task FailReply(ScheduleChannelScenario scenario, Update delivery)
    {
        var count = scenario.Sender.SentMenus.Count;
        scenario.Sender.FailNextSend(new IOException("Simulated reply failure"));
        await Assert.ThrowsAsync<IOException>(() => scenario.Deliver(delivery));
        Assert.Equal(count, scenario.Sender.SentMenus.Count);
    }
}
