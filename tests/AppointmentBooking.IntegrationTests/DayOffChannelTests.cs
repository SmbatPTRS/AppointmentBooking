using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DayOffChannelTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DayOffCreatedInOneChannelCanBeRemovedInTheOther(bool recurring, bool createInTelegram)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        var before = await Slots(customer, scenario, scenario.Date);
        var nextWeek = await Slots(customer, scenario, scenario.Date.AddDays(7));
        Assert.NotEmpty(before);
        Assert.NotEmpty(nextWeek);
        var model = recurring ? new DayOffModel(Weekday: scenario.Date.DayOfWeek) : new DayOffModel(Date: scenario.Date);
        var label = recurring ? "Tuesday" : "2026-11-10";
        if (createInTelegram)
        {
            await scenario.Send("/schedule");
            await scenario.Click("Add day off");
            await scenario.Send(label);
        }
        else
        {
            using var created = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.NotNull(created.Headers.Location);
            var locationRows = (await employee.GetFromJsonAsync<DayOffDto[]>(created.Headers.Location))!;
            Assert.Single(locationRows);
            await scenario.Send("/schedule");
        }
        Assert.Contains("Days off: 1", scenario.LastMenu);
        Assert.Contains(scenario.Sender.SentMenus.Last().Buttons, b => b.Text == "Remove " + label);
        var day = Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(model.Date, day.Date);
        Assert.Equal(model.Weekday, day.Weekday);
        Assert.Empty(await Slots(customer, scenario, scenario.Date));
        if (recurring) Assert.Empty(await Slots(customer, scenario, scenario.Date.AddDays(7)));
        else Assert.Equal(nextWeek, await Slots(customer, scenario, scenario.Date.AddDays(7)));
        Assert.NotEmpty(await Slots(customer, scenario, scenario.Date.AddDays(1)));

        if (createInTelegram)
        {
            using var removed = await employee.DeleteAsync($"{scenario.DaysUrl}/{day.Id}");
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            await scenario.Send("/schedule");
        }
        else
            await scenario.Click("Remove " + label);
        Assert.Contains("Days off: 0", scenario.LastMenu);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, await Slots(customer, scenario, scenario.Date));
        Assert.Equal(nextWeek, await Slots(customer, scenario, scenario.Date.AddDays(7)));
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == scenario.Staff.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateAcrossChannelsPreservesTheOriginalIdAndAllowsCorrection(bool recurring)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        var label = recurring ? "Tuesday" : "2026-11-10";
        var model = recurring ? new DayOffModel(Weekday: DayOfWeek.Tuesday) : new DayOffModel(Date: scenario.Date);
        using var created = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var original = (await created.Content.ReadFromJsonAsync<DayOffDto>())!;
        await scenario.Send("/schedule");
        await scenario.Click("Add day off");
        await scenario.Send(label);
        Assert.Contains("already exists", scenario.LastMenu);
        Assert.Equal(original, Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!));
        using var duplicate = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var json = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.DuplicateDayOff, json.RootElement.GetProperty("code").GetString());
        Assert.Empty(await Slots(customer, scenario, scenario.Date));
        // The Telegram form is still open: correction adds a different day, not another original row.
        await scenario.Send("2026-11-11");
        var rows = (await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!;
        Assert.Equal(2, rows.Length);
        Assert.Contains(original, rows);
        Assert.Contains(rows, d => d.Date == scenario.Date.AddDays(1));
        Assert.Empty(await Slots(customer, scenario, scenario.Date.AddDays(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingDateAndWeekdayStayClosedUntilBothChannelsRemoveTheirRows(bool removeDateFirst)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        var before = await Slots(customer, scenario, scenario.Date);
        await scenario.Send("/schedule");
        await scenario.Click("Add day off");
        await scenario.Send("Tuesday");
        using var added = await employee.PostAsJsonAsync(scenario.DaysUrl, new DayOffModel(Date: scenario.Date));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var dated = (await added.Content.ReadFromJsonAsync<DayOffDto>())!;
        var rows = (await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!;
        var weekly = Assert.Single(rows, d => d.Weekday == DayOfWeek.Tuesday);
        await scenario.Send("/schedule");
        Assert.Contains("Days off: 2", scenario.LastMenu);
        Assert.Empty(await Slots(customer, scenario, scenario.Date));
        using var removed = await employee.DeleteAsync($"{scenario.DaysUrl}/{(removeDateFirst ? dated.Id : weekly.Id)}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty(await Slots(customer, scenario, scenario.Date));
        var following = await Slots(customer, scenario, scenario.Date.AddDays(7));
        if (removeDateFirst) Assert.Empty(following);
        else Assert.NotEmpty(following);
        await scenario.Send("/schedule");
        Assert.Contains("Days off: 1", scenario.LastMenu);
        await scenario.Click("Remove " + (removeDateFirst ? "Tuesday" : "2026-11-10"));
        Assert.Equal(before, await Slots(customer, scenario, scenario.Date));
        Assert.NotEmpty(await Slots(customer, scenario, scenario.Date.AddDays(7)));
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
    }

    [Theory]
    [InlineData("2026-02-30", "{}")]
    [InlineData("2", "{\"weekday\":2,\"date\":\"2026-11-10\"}")]
    [InlineData("2026/11/10", "{\"weekday\":8}")]
    public async Task InvalidDayOffInputsDoNotChangeAvailabilityInEitherChannel(string text, string json)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        var before = await Slots(customer, scenario, scenario.Date);
        await scenario.Send("/schedule");
        await scenario.Click("Add day off");
        await scenario.Send(text);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, await Slots(customer, scenario, scenario.Date));
        using var rejected = await employee.PostAsync(scenario.DaysUrl,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, await Slots(customer, scenario, scenario.Date));
        await scenario.Send("2026-11-10");
        Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Empty(await Slots(customer, scenario, scenario.Date));
    }

    [Fact]
    public async Task TelegramRemovalOfAnAlreadyHttpDeletedDayCannotRemoveAnotherDay()
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await ConfigureHours(employee, scenario);
        using var first = await employee.PostAsJsonAsync(scenario.DaysUrl, new DayOffModel(Date: scenario.Date));
        using var second = await employee.PostAsJsonAsync(scenario.DaysUrl, new DayOffModel(Date: scenario.Date.AddDays(1)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var removed = (await first.Content.ReadFromJsonAsync<DayOffDto>())!;
        var retained = (await second.Content.ReadFromJsonAsync<DayOffDto>())!;
        await scenario.Send("/schedule");
        var oldButton = scenario.Sender.SentMenus.Last().Buttons.Single(b => b.Text == "Remove 2026-11-10").CallbackData;
        using var deleted = await employee.DeleteAsync($"{scenario.DaysUrl}/{removed.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await scenario.Callback(oldButton);
        Assert.Contains("could not be completed", scenario.Sender.SentMessages.Last().Text);
        Assert.Equal(retained, Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!));
        Assert.NotEmpty(await Slots(customer, scenario, scenario.Date));
        Assert.Empty(await Slots(customer, scenario, scenario.Date.AddDays(1)));
        await scenario.Send("/schedule");
        Assert.DoesNotContain(scenario.Sender.SentMenus.Last().Buttons, b => b.Text == "Remove 2026-11-10");
        await scenario.Click("Remove 2026-11-11");
        Assert.NotEmpty(await Slots(customer, scenario, scenario.Date.AddDays(1)));
    }

    private static async Task ConfigureHours(HttpClient employee, ScheduleChannelScenario scenario)
    {
        using var configured = await employee.PutAsJsonAsync(scenario.ScheduleUrl, new WorkingHoursModel(new(9, 0), new(12, 0)));
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
    }

    private static async Task<AvailableSlot[]> Slots(HttpClient customer, ScheduleChannelScenario scenario, DateOnly date) =>
        (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrlFor(date)))!;
}
