using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleBookingChannelTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("opening", false)]
    [InlineData("opening", true)]
    [InlineData("closing", false)]
    [InlineData("closing", true)]
    [InlineData("break", false)]
    [InlineData("break", true)]
    public async Task ConflictingHoursAreRejectedAndCanBeSavedAfterCustomerCancellation(string change, bool telegram)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        var original = new WorkingHoursModel(new(9, 0), new(18, 0));
        await SetHours(employee, scenario, original);
        var booking = await Book(customer, scenario, new(10, 0));
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(before);
        var proposed = change switch
        {
            "opening" => new WorkingHoursModel(new(10, 15), new(18, 0)),
            "closing" => new WorkingHoursModel(new(9, 0), new(10, 15)),
            _ => new WorkingHoursModel(new(9, 0), new(18, 0), new(10, 15), new(11, 0))
        };
        var input = change switch
        {
            "opening" => "10:15 18:00",
            "closing" => "09:00 10:15",
            _ => "09:00 18:00 10:15 11:00"
        };
        if (telegram)
        {
            await scenario.Send("/schedule");
            await scenario.Click("Edit hours");
            await scenario.Send(input);
            Assert.Contains("conflicts with future confirmed bookings", scenario.LastMenu);
        }
        else
        {
            using var rejected = await employee.PutAsJsonAsync(scenario.ScheduleUrl, proposed);
            await AssertConflict(rejected);
        }
        Assert.Equal(original, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await AssertBookingUnchanged(scenario, booking, BookingStatus.Confirmed);

        using var cancelled = await customer.PostAsync($"/api/bookings/{booking.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        if (telegram)
            await scenario.Send(input); // The rejected Telegram form must remain usable.
        else
            await SetHours(employee, scenario, proposed);
        Assert.Equal(proposed, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        await scenario.Send("/schedule");
        Assert.Contains($"Hours: {proposed.OpensAt:HH:mm}–{proposed.ClosesAt:HH:mm}", scenario.LastMenu);
        await AssertBookingUnchanged(scenario, booking, BookingStatus.Cancelled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DayOffUsesLocalBookingDateAndSucceedsOnlyAfterCancellation(bool weekly, bool telegram)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture, "Asia/Yerevan");
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await SetHours(employee, scenario, new WorkingHoursModel(new(0, 0), new(3, 0)));
        var booking = await Book(customer, scenario, new(0, 15));
        // The UTC date/weekday differs: checking UTC instead of business-local time would miss this conflict.
        Assert.Equal(scenario.Date.AddDays(-1), DateOnly.FromDateTime(booking.StartAtUtc));
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.NotEmpty(before);
        var model = weekly ? new DayOffModel(Weekday: DayOfWeek.Tuesday) : new DayOffModel(Date: scenario.Date);
        var input = weekly ? "Tuesday" : "2026-11-10";
        if (telegram)
        {
            await scenario.Send("/schedule");
            await scenario.Click("Add day off");
            await scenario.Send(input);
            Assert.Contains("conflicts with a future confirmed booking", scenario.LastMenu);
        }
        else
        {
            using var rejected = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
            await AssertConflict(rejected);
        }
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        await AssertBookingUnchanged(scenario, booking, BookingStatus.Confirmed);
        using var cancelled = await customer.PostAsync($"/api/bookings/{booking.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        if (telegram)
            await scenario.Send(input);
        else
        {
            using var created = await employee.PostAsJsonAsync(scenario.DaysUrl, model);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var saved = Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        Assert.Equal(model.Date, saved.Date);
        Assert.Equal(model.Weekday, saved.Weekday);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!);
        Assert.NotEmpty((await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrlFor(scenario.Date.AddDays(1))))!);
        await scenario.Send("/schedule");
        Assert.Contains(scenario.Sender.SentMenus.Last().Buttons, b => b.Text == "Remove " + input);
        await AssertBookingUnchanged(scenario, booking, BookingStatus.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOpeningGridAndExactClosingBoundaryPreserveAcceptedBooking(bool telegram)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await SetHours(employee, scenario, new WorkingHoursModel(new(9, 0), new(18, 0)));
        var booking = await Book(customer, scenario, new(10, 0));
        var proposed = new WorkingHoursModel(new(9, 10), new(10, 30));
        if (telegram)
        {
            await scenario.Send("/schedule");
            await scenario.Click("Edit hours");
            await scenario.Send("09:10 10:30");
        }
        else
            await SetHours(employee, scenario, proposed);
        Assert.Equal(proposed, await employee.GetFromJsonAsync<WorkingHoursModel>(scenario.ScheduleUrl));
        var slots = (await customer.GetFromJsonAsync<AvailableSlot[]>(scenario.AvailabilityUrl))!;
        Assert.Equal(new[]
        {
            new AvailableSlot(scenario.At(new(9, 10)), scenario.At(new(9, 40))),
            new AvailableSlot(scenario.At(new(9, 25)), scenario.At(new(9, 55)))
        }, slots);
        // Existing bookings retain their accepted start, even when it is not on the new opening-based grid.
        await AssertBookingUnchanged(scenario, booking, BookingStatus.Confirmed);
        await scenario.Send("/schedule");
        Assert.Contains("Hours: 09:10–10:30", scenario.LastMenu);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeeklyDayOffCannotInvalidateABookingBeyondTheCurrentBookingWindow(bool telegram)
    {
        await using var scenario = await ScheduleChannelScenario.Create(fixture);
        using var employee = scenario.Client(employee: true);
        using var customer = scenario.Client();
        await SetHours(employee, scenario, new WorkingHoursModel(new(9, 0), new(18, 0)));
        var futureDate = scenario.Date.AddDays(56); // Same weekday, well beyond the 30-day selection window.
        var start = TimeZoneInfo.ConvertTimeToUtc(futureDate.ToDateTime(new(10, 0), DateTimeKind.Unspecified), scenario.Zone);
        Guid bookingId;
        await using (var db = fixture.CreateContext())
        {
            // Arrange an already accepted future booking (e.g. from an earlier, larger booking window).
            var booking = new Booking(scenario.Customer.Id, scenario.Staff.Id, scenario.Offering.Id,
                Guid.NewGuid(), start, 20m, 30, new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc));
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            bookingId = booking.Id;
        }
        using var outsideWindow = await customer.GetAsync(scenario.AvailabilityUrlFor(futureDate));
        Assert.Equal(HttpStatusCode.BadRequest, outsideWindow.StatusCode);
        if (telegram)
        {
            await scenario.Send("/schedule");
            await scenario.Click("Add day off");
            await scenario.Send("Tuesday");
            Assert.Contains("conflicts with a future confirmed booking", scenario.LastMenu);
        }
        else
        {
            using var rejected = await employee.PostAsJsonAsync(scenario.DaysUrl, new DayOffModel(Weekday: DayOfWeek.Tuesday));
            await AssertConflict(rejected);
        }
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(scenario.DaysUrl))!);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, saved.Status);
        Assert.Equal(start, saved.StartsAtUtc);
        Assert.Equal(start.AddMinutes(30), saved.EndsAtUtc);
    }

    private static async Task SetHours(HttpClient employee, ScheduleChannelScenario scenario, WorkingHoursModel hours)
    {
        using var response = await employee.PutAsJsonAsync(scenario.ScheduleUrl, hours);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<BookingDto> Book(HttpClient customer, ScheduleChannelScenario scenario, TimeOnly time)
    {
        using var response = await customer.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), scenario.Customer.Id, scenario.Offering.Id, scenario.At(time).UtcDateTime, 20m, 30));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookingDto>())!;
    }

    private static async Task AssertConflict(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.ScheduleConflictsWithBookings, json.RootElement.GetProperty("code").GetString());
    }

    private async Task AssertBookingUnchanged(ScheduleChannelScenario scenario, BookingDto original, BookingStatus status)
    {
        await using var db = fixture.CreateContext();
        var saved = Assert.Single(await db.Bookings.Where(b => b.StaffMemberId == scenario.Staff.Id).ToListAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.StartAtUtc, saved.StartsAtUtc);
        Assert.Equal(original.EndAtUtc, saved.EndsAtUtc);
        Assert.Equal(original.Price, saved.Price);
        Assert.Equal(original.DurationMinutes, saved.DurationMinutes);
        Assert.Equal(status, saved.Status);
    }
}
