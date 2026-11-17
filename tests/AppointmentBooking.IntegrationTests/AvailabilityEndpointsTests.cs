using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Infrastructure.Transactions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AvailabilityEndpointsTests(PostgresFixture fixture)
{
    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed record Setup(ApiFactory Factory, WebApplicationFactory<Program> Host,
        User ClientUser, StaffMember Staff, StaffService Offering, TimeZoneInfo Zone) : IAsyncDisposable
    {
        public string Url(string date = "2026-11-10") =>
            $"/api/availability?staffId={Staff.Id}&staffServiceId={Offering.Id}&date={date}";
        public HttpClient Client(string? key = "availability-test")
        {
            var client = Host.CreateClient();
            if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
            return client;
        }
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Factory.DisposeAsync(); }
    }

    private async Task<Setup> SeedAsync(int duration = 40, LockGate? gate = null)
    {
        await using var db = fixture.CreateContext();
        var clock = new Clock();
        var customer = new User(Random.Shared.NextInt64(6000000000, 9000000000), "Availability customer", null, clock.UtcNow);
        var employee = new User(Random.Shared.NextInt64(6000000000, 9000000000), "Availability staff", null, clock.UtcNow);
        var staff = new StaffMember(employee.Id, "Availability staff");
        var service = new Service("Availability test");
        var offering = new StaffService(staff.Id, service.Id, 100m, duration);
        var business = await db.BusinessSettings.SingleOrDefaultAsync();
        if (business is null) db.Add(business = new BusinessSettings("Studio", "Staff", "Asia/Yerevan", "AMD"));
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == business.Id)) db.Add(new BookingCoordination(business));
        db.AddRange(customer, employee, staff, service, offering, new BookingCoordination(staff),
            new WorkingHours(staff.Id, new(9, 0), new(18, 0), new(12, 0), new(13, 0)));
        await db.SaveChangesAsync();
        var factory = new ApiFactory(fixture.ConnectionString);
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiAccess:Keys:0:Key"] = "availability-test",
                ["ApiAccess:Keys:0:TelegramUserId"] = customer.TelegramUserId.ToString(),
                ["ApiAccess:Keys:1:Key"] = "availability-employee",
                ["ApiAccess:Keys:1:TelegramUserId"] = employee.TelegramUserId.ToString(),
                ["Booking:SlotStepMinutes"] = "15", ["Booking:MaxAdvanceDays"] = "30"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(clock);
                if (gate is not null)
                    services.AddScoped<IStaffOperationLock>(provider => new GatedStaffLock(
                        new PostgresStaffOperationLock(provider.GetRequiredService<AppDbContext>()), gate));
            });
        });
        return new(factory, host, customer, staff, offering, TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId));
    }

    private sealed class LockGate
    {
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;
    }

    private sealed class GatedStaffLock(PostgresStaffOperationLock inner, LockGate gate) : IStaffOperationLock
    {
        public async Task AcquireAsync(Guid staffId, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref gate.Attempts);
            await inner.AcquireAsync(staffId, ct);
            if (attempt == 1)
            {
                gate.Held.TrySetResult();
                await gate.Release.Task.WaitAsync(ct);
            }
        }
    }

    public enum ScheduleEdit { ShortenHours, DateOff, WeeklyDayOff }

    [Theory]
    [InlineData(true, ScheduleEdit.ShortenHours)]
    [InlineData(false, ScheduleEdit.ShortenHours)]
    [InlineData(true, ScheduleEdit.DateOff)]
    [InlineData(false, ScheduleEdit.DateOff)]
    [InlineData(true, ScheduleEdit.WeeklyDayOff)]
    [InlineData(false, ScheduleEdit.WeeklyDayOff)]
    public async Task BookingAndConflictingScheduleEditRecheckStateUnderTheSameLock(bool bookingFirst, ScheduleEdit change)
    {
        var gate = new LockGate();
        await using var setup = await SeedAsync(30, gate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        // Recurring days off must protect bookings several weeks ahead, too.
        var date = new DateOnly(2026, 11, change == ScheduleEdit.WeeklyDayOff ? 30 : 10);
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(16, 0)), setup.Zone);
        var request = new CreateBookingRequest(Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id, start, 100m, 30);
        var proposed = new WorkingHoursModel(new(9, 0), new(16, 0));
        await using var bookingScope = setup.Host.Services.CreateAsyncScope();
        await using var scheduleScope = setup.Host.Services.CreateAsyncScope();
        await using var db = fixture.CreateContext();
        var bookings = bookingScope.ServiceProvider.GetRequiredService<BookingService>();
        var schedules = scheduleScope.ServiceProvider.GetRequiredService<ScheduleService>();
        Task Book() => bookings.CreateAsync(new Actor(setup.ClientUser.Id), request, ct);
        var employee = new Actor(setup.Staff.UserId);
        Task Edit() => change switch
        {
            ScheduleEdit.DateOff => schedules.AddDayOffAsync(employee, setup.Staff.Id, new DayOffModel(Date: date), ct),
            ScheduleEdit.WeeklyDayOff => schedules.AddDayOffAsync(employee, setup.Staff.Id, new DayOffModel(Weekday: date.DayOfWeek), ct),
            _ => schedules.SetHoursAsync(employee, setup.Staff.Id, proposed, ct)
        };
        Task first = bookingFirst ? Book() : Edit();
        Task? second = null;

        try
        {
            await Task.WhenAny(first, gate.Held.Task).WaitAsync(ct);
            if (first.IsCompleted) await first;
            Assert.True(gate.Held.Task.IsCompletedSuccessfully, "The first operation must acquire the employee lock.");
            second = bookingFirst ? Edit() : Book();
            while (!await db.Database.SqlQueryRaw<bool>(
                """SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted) AS "Value" """)
                .SingleAsync(ct))
            {
                Assert.False(second.IsCompleted, "The second operation must wait for the employee lock.");
                await Task.Delay(10, ct);
            }
        }
        catch
        {
            await timeout.CancelAsync();
            try { await Task.WhenAll(first, second ?? Task.CompletedTask); }
            catch { /* Preserve the original assertion or cancellation. */ }
            throw;
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await first;
        var error = await Assert.ThrowsAsync<AppException>(() => second!);
        Assert.Equal(AppErrorKind.Conflict, error.Kind);
        Assert.Equal(bookingFirst ? ErrorCodes.ScheduleConflictsWithBookings : ErrorCodes.TimeUnavailable, error.Code);

        var hours = await db.WorkingHours.SingleAsync(h => h.StaffMemberId == setup.Staff.Id, ct);
        var stored = await db.Bookings.Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync(ct);
        Assert.Equal(new TimeOnly(!bookingFirst && change == ScheduleEdit.ShortenHours ? 16 : 18, 0), hours.ClosesAt);

        var daysOff = await db.StaffDaysOff.Where(d => d.StaffMemberId == setup.Staff.Id).ToListAsync(ct);
        if (bookingFirst || change == ScheduleEdit.ShortenHours)
            Assert.Empty(daysOff);
        else
        {
            var saved = Assert.Single(daysOff);
            Assert.Equal(change == ScheduleEdit.DateOff ? date : (DateOnly?)null, saved.Date);
            Assert.Equal(change == ScheduleEdit.WeeklyDayOff ? date.DayOfWeek : (DayOfWeek?)null, saved.Weekday);
        }

        if (bookingFirst)
            Assert.Equal(start, Assert.Single(stored).StartsAtUtc);
        else
            Assert.Empty(stored);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            if (code is not null)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task RequiresAuthenticatedUser(string? key)
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client(key);
        await AssertError(await client.GetAsync(setup.Url()), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&date=2026-11-10")]
    [InlineData("&date=2026-02-30")]
    [InlineData("&date=11-10-2026")]
    [InlineData("&date=2026-11-10T00:00:00Z")]
    [InlineData("&date=2026-1-1")]
    public async Task MissingOrNonIsoDateReturnsBadRequest(string suffix)
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        var url = suffix == "&date=2026-11-10" ? "/api/availability?staffId=" + setup.Staff.Id + suffix
            : $"/api/availability?staffId={setup.Staff.Id}&staffServiceId={setup.Offering.Id}" + suffix;
        await AssertError(await client.GetAsync(url), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListsUtcIntervalsOnOpeningGridExcludingBreakAndClosingOverflow()
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        var slots = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        Assert.NotEmpty(slots);
        Assert.Equal(slots.OrderBy(s => s.StartsAtUtc), slots);
        foreach (var slot in slots)
        {
            Assert.Equal(TimeSpan.Zero, slot.StartsAtUtc.Offset);
            Assert.Equal(TimeSpan.FromMinutes(40), slot.EndsAtUtc - slot.StartsAtUtc);
            var start = TimeZoneInfo.ConvertTime(slot.StartsAtUtc, setup.Zone);
            var end = TimeZoneInfo.ConvertTime(slot.EndsAtUtc, setup.Zone);
            Assert.Equal(new DateOnly(2026, 11, 10), DateOnly.FromDateTime(start.DateTime));
            Assert.InRange(start.Hour, 9, 17);
            Assert.Equal(0, (start.Hour * 60 + start.Minute - 540) % 15);
            Assert.True(end.TimeOfDay <= TimeSpan.FromHours(18));
            Assert.True(end.TimeOfDay <= TimeSpan.FromHours(12) || start.TimeOfDay >= TimeSpan.FromHours(13));
        }
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var availability = scope.ServiceProvider.GetRequiredService<AvailabilityService>();
        Assert.True(await availability.IsAvailableAsync(setup.Staff.Id, setup.Offering.Id, slots[0].StartsAtUtc, default));
        Assert.False(await availability.IsAvailableAsync(setup.Staff.Id, setup.Offering.Id, slots[0].StartsAtUtc.AddMinutes(1), default));
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("service")]
    [InlineData("offering")]
    [InlineData("hours")]
    [InlineData("weekday")]
    [InlineData("date")]
    public async Task ValidNonworkingDateReturnsEmptyArray(string reason)
    {
        await using var setup = await SeedAsync();
        await using var db = fixture.CreateContext();
        switch (reason)
        {
            case "staff": (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate(); break;
            case "service": (await db.Services.SingleAsync(s => s.Id == setup.Offering.ServiceId)).Deactivate(); break;
            case "offering": (await db.StaffServices.SingleAsync(s => s.Id == setup.Offering.Id)).Deactivate(); break;
            case "hours": db.Remove(await db.WorkingHours.SingleAsync(s => s.StaffMemberId == setup.Staff.Id)); break;
            case "weekday": db.Add(new StaffDayOff(setup.Staff.Id, DayOfWeek.Tuesday, null)); break;
            case "date": db.Add(new StaffDayOff(setup.Staff.Id, null, new(2026, 11, 10))); break;
        }
        await db.SaveChangesAsync();
        using var client = setup.Client();
        Assert.Empty((await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
    }

    [Fact]
    public async Task MissingAndMismatchedResourcesUseSharedErrors()
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        await AssertError(await client.GetAsync(setup.Url().Replace(setup.Staff.Id.ToString(), Guid.NewGuid().ToString())), HttpStatusCode.NotFound, ErrorCodes.StaffNotFound);
        await AssertError(await client.GetAsync(setup.Url().Replace(setup.Offering.Id.ToString(), Guid.NewGuid().ToString())), HttpStatusCode.NotFound, ErrorCodes.StaffServiceNotFound);
        await using var other = await SeedAsync();
        await AssertError(await client.GetAsync(setup.Url().Replace(setup.Offering.Id.ToString(), other.Offering.Id.ToString())), HttpStatusCode.BadRequest, ErrorCodes.StaffServiceMismatch);
    }

    [Fact]
    public async Task BookingAndCancellationChangeAvailabilityWithoutRestart()
    {
        await using var setup = await SeedAsync(30);
        using var client = setup.Client();
        var before = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var selected = before[2];
        var request = new CreateBookingRequest(Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id,
            selected.StartsAtUtc.UtcDateTime, 100m, 30);
        using var createdResponse = await client.PostAsJsonAsync("/api/bookings", request);
        createdResponse.EnsureSuccessStatusCode();
        var created = (await createdResponse.Content.ReadFromJsonAsync<BookingDto>())!;
        var during = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        Assert.Equal(before.Where(s => s.EndsAtUtc <= selected.StartsAtUtc || s.StartsAtUtc >= selected.EndsAtUtc), during);
        await using (var scope = setup.Host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BookingService>().CancelAsync(new Actor(setup.ClientUser.Id), created.Id, default);
        Assert.Equal(before, (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
    }
    [Fact]
    public async Task BookingWindowIncludesLastDayButRejectsPastAndFollowingDay()
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(new Clock().UtcNow, setup.Zone));
        Assert.NotEmpty((await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url(today.AddDays(30).ToString("yyyy-MM-dd"))))!);
        foreach (var date in new[] { today.AddDays(-1), today.AddDays(31) })
            await AssertError(await client.GetAsync(setup.Url(date.ToString("yyyy-MM-dd"))), HttpStatusCode.BadRequest, ErrorCodes.BookingOutsideWindow);
    }

    [Theory]
    [InlineData("staffId")]
    [InlineData("staffServiceId")]
    public async Task IdentifiersMustBePresentAndNonempty(string name)
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        var value = name == "staffId" ? setup.Staff.Id : setup.Offering.Id;
        await AssertError(await client.GetAsync(setup.Url().Replace(name + "=" + value, name + "=" + Guid.Empty)), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await AssertError(await client.GetAsync(setup.Url().Replace(name + "=" + value, name + "=invalid")), HttpStatusCode.BadRequest);
        await AssertError(await client.GetAsync(setup.Url().Replace(name + "=" + value, "unused=1")), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingDayOffRestoresSlotsOnlyForTheEmployeeWhoRemovedIt(bool recurring)
    {
        await using var first = await SeedAsync(30);
        await using var second = await SeedAsync(30);
        using var customer = first.Client();
        using var employee = first.Client("availability-employee");
        using var otherEmployee = second.Client("availability-employee");
        var date = new DateOnly(2026, 11, 10);
        var day = recurring ? new DayOffModel(Weekday: date.DayOfWeek) : new DayOffModel(Date: date);
        var firstDays = $"/api/staff/{first.Staff.Id}/days-off";
        var secondDays = $"/api/staff/{second.Staff.Id}/days-off";

        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!;
        Assert.NotEmpty(before);
        Assert.NotEmpty((await customer.GetFromJsonAsync<AvailableSlot[]>(second.Url()))!);

        using var firstAdded = await employee.PostAsJsonAsync(firstDays, day);
        using var secondAdded = await otherEmployee.PostAsJsonAsync(secondDays, day);
        Assert.Equal(HttpStatusCode.Created, firstAdded.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondAdded.StatusCode);
        var firstOff = (await firstAdded.Content.ReadFromJsonAsync<DayOffDto>())!;
        var secondOff = (await secondAdded.Content.ReadFromJsonAsync<DayOffDto>())!;
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(second.Url()))!);

        // A foreign day-off ID must not be usable even under the caller's own staff route.
        await AssertError(await employee.DeleteAsync($"{firstDays}/{secondOff.Id}"),
            HttpStatusCode.NotFound, ErrorCodes.DayOffNotFound);
        await AssertError(await employee.DeleteAsync($"{secondDays}/{secondOff.Id}"),
            HttpStatusCode.Forbidden);
        using var deleted = await employee.DeleteAsync($"{firstDays}/{firstOff.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!);
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(second.Url()))!);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(firstDays))!);
        Assert.Equal(secondOff, Assert.Single((await otherEmployee.GetFromJsonAsync<DayOffDto[]>(secondDays))!));
    }

    [Fact]
    public async Task BookingForAnotherEmployeeDoesNotHideSelectedEmployeesSlots()
    {
        await using var first = await SeedAsync(30);
        await using var second = await SeedAsync(30);
        using var client = first.Client();
        var before = (await client.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!;
        var start = before[0].StartsAtUtc.UtcDateTime;
        using var response = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), first.ClientUser.Id, second.Offering.Id, start, 100m, 30));
        response.EnsureSuccessStatusCode();
        Assert.Equal(before, (await client.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!);
    }

    [Fact]
    public async Task ClosingBoundaryIsIncludedAndTooLongDurationHasNoSlots()
    {
        await using var setup = await SeedAsync(30);
        using var client = setup.Client();
        var slots = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var last = TimeZoneInfo.ConvertTime(slots[^1].EndsAtUtc, setup.Zone);
        Assert.Equal(new TimeOnly(18, 0), TimeOnly.FromDateTime(last.DateTime));
        await using var db = fixture.CreateContext();
        var offering = await db.StaffServices.SingleAsync(o => o.Id == setup.Offering.Id);
        offering.UpdateTerms(100m, 600);
        await db.SaveChangesAsync();
        Assert.Empty((await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
    }
}
