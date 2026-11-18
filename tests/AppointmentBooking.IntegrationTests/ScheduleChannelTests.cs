using System.Net;
using System.Net.Http.Json;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleChannelTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Asia/Yerevan")]
    [InlineData("America/Los_Angeles")]
    public async Task TelegramHoursProduceExactHttpSlotsInTheBusinessTimezone(string zone)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture, zone);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        Assert.Null(await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await scenario.Send("/schedule");
        Assert.Contains("not configured", scenario.LastMenu);
        await scenario.Click("Edit hours");
        await scenario.Send("00:10 04:10 01:10 02:10");
        Assert.Contains("Hours: 00:10–04:10", scenario.LastMenu);
        Assert.Contains("Break: 01:10–02:10", scenario.LastMenu);
        var hours = await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl);
        Assert.Equal(new WorkingHoursModel(new(0, 10), new(4, 10), new(1, 10), new(2, 10)), hours);

        // Independent expected list: opening-based grid, not quarter-hour wall-clock alignment.
        var expected = new[] { new TimeOnly(0, 10), new(0, 25), new(0, 40), new(2, 10),
            new(2, 25), new(2, 40), new(2, 55), new(3, 10), new(3, 25), new(3, 40) }
            .Select(start => new AvailableSlot(scenario.At(start), scenario.At(start).AddMinutes(30))).ToArray();
        var actual = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.Equal(expected, actual);
        Assert.All(actual, slot => Assert.Equal(TimeSpan.Zero, slot.StartsAtUtc.Offset));
        Assert.Equal(scenario.Date, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(actual[0].StartsAtUtc, scenario.Zone).DateTime));
        Assert.Equal(zone == "Asia/Yerevan" ? scenario.Date.AddDays(-1) : scenario.Date,
            DateOnly.FromDateTime(actual[0].StartsAtUtc.UtcDateTime));
    }

    [Fact]
    public async Task HttpHoursReplaceTelegramHoursAndFreshOverviewShowsTheNewBreak()
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await scenario.Send("/schedule");
        await scenario.Click("Edit hours");
        await scenario.Send("09:00 18:00 12:00 13:00");
        var original = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.Contains(original, s => s.StartsAtUtc == scenario.At(new(9, 0)));
        var replacement = new WorkingHoursModel(new(10, 0), new(16, 0), new(13, 0), new(14, 0));
        using var updated = await employee.PutAsJsonAsync(scenario.ScheduleUrl, replacement);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        await scenario.Send("/schedule");
        Assert.Contains("Hours: 10:00–16:00", scenario.LastMenu);
        Assert.Contains("Break: 13:00–14:00", scenario.LastMenu);
        var slots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(slots);
        Assert.Equal(scenario.At(new(10, 0)), slots[0].StartsAtUtc);
        Assert.Equal(scenario.At(new(16, 0)), slots[^1].EndsAtUtc);
        Assert.All(slots, slot => Assert.True(slot.EndsAtUtc <= scenario.At(new(13, 0)) ||
            slot.StartsAtUtc >= scenario.At(new(14, 0))));
        await using var db = fixture.CreateContext();
        Assert.Single(await db.WorkingHours.Where(h => h.StaffMemberId == scenario.Staff.Id).ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BreakCanBeRemovedThroughEitherChannelWithoutCreatingAnotherHoursRow(bool throughTelegram)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        using var configured = await employee.PutAsJsonAsync(scenario.ScheduleUrl,
            new WorkingHoursModel(new(9, 0), new(14, 0), new(11, 0), new(12, 0)));
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.DoesNotContain(before, s => s.StartsAtUtc == scenario.At(new(11, 0)));
        await using var db = fixture.CreateContext();
        var id = (await db.WorkingHours.AsNoTracking().SingleAsync(h => h.StaffMemberId == scenario.Staff.Id)).Id;
        if (throughTelegram)
        {
            await scenario.Send("/schedule");
            await scenario.Click("Edit hours");
            await scenario.Send("09:00 14:00");
        }
        else
        {
            using var updated = await employee.PutAsJsonAsync(scenario.ScheduleUrl, new WorkingHoursModel(new(9, 0), new(14, 0)));
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
            await scenario.Send("/schedule");
        }
        Assert.DoesNotContain("Break:", scenario.LastMenu);
        var after = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.Contains(after, s => s.StartsAtUtc == scenario.At(new(11, 0)));
        Assert.All(before, s => Assert.Contains(s, after));
        var saved = await db.WorkingHours.AsNoTracking().SingleAsync(h => h.StaffMemberId == scenario.Staff.Id);
        Assert.Equal(id, saved.Id);
        Assert.Null(saved.BreakStartsAt);
        Assert.Null(saved.BreakEndsAt);
    }

    [Theory]
    [InlineData("18:00 09:00", "{\"opensAt\":\"18:00:00\",\"closesAt\":\"09:00:00\"}")]
    [InlineData("09:00 18:00 13:00", "{\"opensAt\":\"09:00:00\",\"closesAt\":\"18:00:00\",\"breakStartsAt\":\"13:00:00\"}")]
    public async Task InvalidEditsInEitherChannelPreserveHoursAndAvailableSlots(string telegram, string json)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        var original = new WorkingHoursModel(new(9, 0), new(18, 0));
        using var configured = await employee.PutAsJsonAsync(scenario.ScheduleUrl, original);
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        await scenario.Send("/schedule");
        await scenario.Click("Edit hours");
        await scenario.Send(telegram);
        Assert.Equal(original, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        using var rejected = await employee.PutAsync(scenario.ScheduleUrl,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(original, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        // The Telegram form remains usable after both invalid requests.
        await scenario.Send("10:00 17:00");
        Assert.Equal(new WorkingHoursModel(new(10, 0), new(17, 0)),
            await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
    }
}
