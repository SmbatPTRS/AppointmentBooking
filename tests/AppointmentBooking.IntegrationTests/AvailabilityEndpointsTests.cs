using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Infrastructure.Transactions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Catalog;
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

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    public async Task ChangedOfferingDurationRefreshesSlotsAndPreservesExistingBooking(int newDuration)
    {
        await using var setup = await SeedAsync(30);
        using var client = setup.Client();
        var before = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var selected = before[0];
        var request = new CreateBookingRequest(Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id,
            selected.StartsAtUtc.UtcDateTime, 100m, 30);
        using var booked = await client.PostAsJsonAsync("/api/bookings", request);
        Assert.Equal(HttpStatusCode.OK, booked.StatusCode);
        var original = (await booked.Content.ReadFromJsonAsync<BookingDto>())!;

        // Establish changed catalogue data using A's entity contract.
        await using (var db = fixture.CreateContext())
        {
            var offering = await db.StaffServices.SingleAsync(o => o.Id == setup.Offering.Id);
            offering.UpdateTerms(250m, newDuration);
            await db.SaveChangesAsync();
        }

        var after = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        Assert.NotEmpty(after);
        Assert.All(after, slot =>
        {
            Assert.Equal(TimeSpan.FromMinutes(newDuration), slot.EndsAtUtc - slot.StartsAtUtc);
            Assert.True(slot.EndsAtUtc.UtcDateTime <= original.StartAtUtc ||
                        slot.StartsAtUtc.UtcDateTime >= original.EndAtUtc);
        });
        Assert.Contains(after, slot => slot.StartsAtUtc.UtcDateTime == original.EndAtUtc);
        var lastLocal = TimeZoneInfo.ConvertTime(after.Last().EndsAtUtc, setup.Zone);
        Assert.Equal(new TimeOnly(18, 0), TimeOnly.FromDateTime(lastLocal.DateTime));

        var newStart = after[0].StartsAtUtc.UtcDateTime;
        using var stale = await client.PostAsJsonAsync("/api/bookings",
            new CreateBookingRequest(Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id, newStart, 100m, 30));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var current = await client.PostAsJsonAsync("/api/bookings",
            new CreateBookingRequest(Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id, newStart, 250m, newDuration));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var created = (await current.Content.ReadFromJsonAsync<BookingDto>())!;
        Assert.Equal(250m, created.Price);
        Assert.Equal(newDuration, created.DurationMinutes);

        await using var verify = fixture.CreateContext();
        var saved = await verify.Bookings.SingleAsync(b => b.Id == original.Id);
        Assert.Equal(100m, saved.Price);
        Assert.Equal(30, saved.DurationMinutes);
        Assert.Equal(original.StartAtUtc, saved.StartsAtUtc);
        Assert.Equal(original.EndAtUtc, saved.EndsAtUtc);
        Assert.Equal(2, await verify.Bookings.CountAsync(b => b.StaffMemberId == setup.Staff.Id));
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("service")]
    [InlineData("offering")]
    public async Task ReactivationRestoresOnlyUnoccupiedSlotsAndPreservesBookingSnapshots(string target)
    {
        await using var setup = await SeedAsync(30);
        using var client = setup.Client();
        var before = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var selected = before[1];
        using var response = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id, selected.StartsAtUtc.UtcDateTime, 100m, 30));
        response.EnsureSuccessStatusCode();
        var booking = (await response.Content.ReadFromJsonAsync<BookingDto>())!;
        var occupied = (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        Assert.NotEmpty(occupied);
        Assert.DoesNotContain(occupied, s => s.StartsAtUtc == selected.StartsAtUtc);

        async Task SetActive(bool active)
        {
            // Fixture setup only; this does not exercise management commands or coordination versions.
            await using var db = fixture.CreateContext();
            switch (target)
            {
                case "staff":
                    var staff = await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id);
                    if (active) staff.Activate(); else staff.Deactivate();
                    break;
                case "service":
                    var service = await db.Services.SingleAsync(s => s.Id == setup.Offering.ServiceId);
                    if (active) service.Activate(); else service.Deactivate();
                    break;
                case "offering":
                    var offering = await db.StaffServices.SingleAsync(s => s.Id == setup.Offering.Id);
                    if (active) offering.Activate(); else offering.Deactivate();
                    break;
            }
            await db.SaveChangesAsync();
        }

        await SetActive(false);
        Assert.Empty((await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
        Assert.Empty((await client.GetFromJsonAsync<StaffServiceDto[]>(
            $"/api/staff/{setup.Staff.Id}/services"))!);
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(b => b.Id == booking.Id);
            Assert.Equal(booking.Price, saved.Price);
            Assert.Equal(booking.DurationMinutes, saved.DurationMinutes);
            Assert.Equal(booking.StartAtUtc, saved.StartsAtUtc);
            Assert.Equal(booking.EndAtUtc, saved.EndsAtUtc);
            Assert.Equal(booking.Status, saved.Status);
        }

        await SetActive(true);
        Assert.Equal(occupied, (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
        var restored = Assert.Single((await client.GetFromJsonAsync<StaffServiceDto[]>(
            $"/api/staff/{setup.Staff.Id}/services"))!);
        Assert.Equal(setup.Offering.Id, restored.Id);
        await using (var scope = setup.Host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BookingService>()
                .CancelAsync(new Actor(setup.ClientUser.Id), booking.Id, default);
        Assert.Equal(before, (await client.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
    }

    [Fact]
    public async Task SharedServiceUsesEachEmployeesTermsAndIndependentAvailability()
    {
        await using var first = await SeedAsync(30);
        await using var second = await SeedAsync(60);
        Guid secondOfferingId;
        await using (var db = fixture.CreateContext())
        {
            var offering = new StaffService(second.Staff.Id, first.Offering.ServiceId, 275m, 45);
            db.StaffServices.Add(offering);
            await db.SaveChangesAsync();
            secondOfferingId = offering.Id;
        }
        using var customer = first.Client();
        var firstCatalogue = (await customer.GetFromJsonAsync<StaffServiceDto[]>(
            $"/api/staff/{first.Staff.Id}/services"))!;
        var secondCatalogue = (await customer.GetFromJsonAsync<StaffServiceDto[]>(
            $"/api/staff/{second.Staff.Id}/services"))!;
        var firstDto = Assert.Single(firstCatalogue, s => s.ServiceId == first.Offering.ServiceId);
        var secondDto = Assert.Single(secondCatalogue, s => s.ServiceId == first.Offering.ServiceId);
        Assert.Equal(firstDto.ServiceName, secondDto.ServiceName);
        Assert.Equal(100m, firstDto.Price);
        Assert.Equal(30, firstDto.DurationMinutes);
        Assert.Equal(275m, secondDto.Price);
        Assert.Equal(45, secondDto.DurationMinutes);
        Assert.NotEqual(firstDto.Id, secondDto.Id);

        var secondUrl = second.Url().Replace(second.Offering.Id.ToString(), secondOfferingId.ToString());
        var firstBefore = (await customer.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!;
        var secondBefore = (await customer.GetFromJsonAsync<AvailableSlot[]>(secondUrl))!;
        Assert.All(firstBefore, s => Assert.Equal(TimeSpan.FromMinutes(30), s.EndsAtUtc - s.StartsAtUtc));
        Assert.All(secondBefore, s => Assert.Equal(TimeSpan.FromMinutes(45), s.EndsAtUtc - s.StartsAtUtc));
        Assert.NotEmpty(firstBefore);
        Assert.NotEmpty(secondBefore);
        var start = firstBefore[0].StartsAtUtc;
        Assert.Equal(start, secondBefore[0].StartsAtUtc);

        using var firstResponse = await customer.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), first.ClientUser.Id, first.Offering.Id, start.UtcDateTime, 100m, 30));
        firstResponse.EnsureSuccessStatusCode();
        Assert.Equal(secondBefore, (await customer.GetFromJsonAsync<AvailableSlot[]>(secondUrl))!);
        using var secondResponse = await customer.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), first.ClientUser.Id, secondOfferingId, start.UtcDateTime, 275m, 45));
        secondResponse.EnsureSuccessStatusCode();
        var booked = (await secondResponse.Content.ReadFromJsonAsync<BookingDto>())!;
        Assert.Equal(275m, booked.Price);
        Assert.Equal(45, booked.DurationMinutes);
        Assert.Equal(start.AddMinutes(45).UtcDateTime, booked.EndAtUtc);

        var firstAfter = (await customer.GetFromJsonAsync<AvailableSlot[]>(first.Url()))!;
        var secondAfter = (await customer.GetFromJsonAsync<AvailableSlot[]>(secondUrl))!;
        Assert.Equal(firstBefore.Where(s => s.StartsAtUtc >= start.AddMinutes(30)), firstAfter);
        Assert.Equal(secondBefore.Where(s => s.StartsAtUtc >= start.AddMinutes(45)), secondAfter);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Bookings.Where(b => b.StaffMemberId == first.Staff.Id ||
            b.StaffMemberId == second.Staff.Id).ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, b => Assert.Equal(start.UtcDateTime, b.StartsAtUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingHoursEditLeavesAvailabilityIntactAndSucceedsAfterCancellation(bool moveBreak)
    {
        await using var setup = await SeedAsync(30);
        using var customer = setup.Client();
        using var employee = setup.Client("availability-employee");
        var scheduleUrl = $"/api/staff/{setup.Staff.Id}/schedule";
        var oldHours = (await employee.GetFromJsonAsync<WorkingHoursModel>(scheduleUrl))!;
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var selected = before.Single(s => TimeZoneInfo.ConvertTime(s.StartsAtUtc, setup.Zone).TimeOfDay ==
            TimeSpan.FromHours(10));
        using var booked = await customer.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), setup.ClientUser.Id, setup.Offering.Id, selected.StartsAtUtc.UtcDateTime, 100m, 30));
        booked.EnsureSuccessStatusCode();
        var booking = (await booked.Content.ReadFromJsonAsync<BookingDto>())!;
        var occupied = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var proposed = moveBreak
            ? new WorkingHoursModel(new(9, 0), new(18, 0), new(10, 15), new(11, 0))
            : new WorkingHoursModel(new(10, 15), new(18, 0), new(12, 0), new(13, 0));

        await AssertError(await employee.PutAsJsonAsync(scheduleUrl, proposed),
            HttpStatusCode.Conflict, ErrorCodes.ScheduleConflictsWithBookings);
        Assert.Equal(oldHours, await employee.GetFromJsonAsync<WorkingHoursModel>(scheduleUrl));
        Assert.Equal(occupied, (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(b => b.Id == booking.Id);
            Assert.Equal(booking.Status, saved.Status);
            Assert.Equal(booking.StartAtUtc, saved.StartsAtUtc);
            Assert.Equal(booking.EndAtUtc, saved.EndsAtUtc);
        }

        await using (var scope = setup.Host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BookingService>()
                .CancelAsync(new Actor(setup.ClientUser.Id), booking.Id, default);
        using var updated = await employee.PutAsJsonAsync(scheduleUrl, proposed);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.Equal(proposed, await employee.GetFromJsonAsync<WorkingHoursModel>(scheduleUrl));
        var after = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        Assert.NotEmpty(after);
        Assert.DoesNotContain(after, s => s.StartsAtUtc == selected.StartsAtUtc);
        Assert.All(after, slot =>
        {
            var start = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.StartsAtUtc, setup.Zone).DateTime);
            var end = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.EndsAtUtc, setup.Zone).DateTime);
            Assert.True(start >= proposed.OpensAt && end <= proposed.ClosesAt);
            Assert.True(end <= proposed.BreakStartsAt || start >= proposed.BreakEndsAt);
        });
        Assert.Contains(after, s => TimeZoneInfo.ConvertTime(s.StartsAtUtc, setup.Zone).TimeOfDay ==
            (moveBreak ? TimeSpan.FromHours(11) : new TimeSpan(10, 15, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingOneOfOverlappingDaysOffDoesNotPrematurelyRestoreAvailability(bool removeWeeklyFirst)
    {
        await using var setup = await SeedAsync(30);
        using var customer = setup.Client();
        using var employee = setup.Client("availability-employee");
        var daysUrl = $"/api/staff/{setup.Staff.Id}/days-off";
        var before = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        Assert.NotEmpty(before);
        using var weeklyResponse = await employee.PostAsJsonAsync(daysUrl, new DayOffModel(Weekday: DayOfWeek.Tuesday));
        using var dateResponse = await employee.PostAsJsonAsync(daysUrl, new DayOffModel(Date: new(2026, 11, 10)));
        Assert.Equal(HttpStatusCode.Created, weeklyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, dateResponse.StatusCode);
        var weekly = (await weeklyResponse.Content.ReadFromJsonAsync<DayOffDto>())!;
        var specific = (await dateResponse.Content.ReadFromJsonAsync<DayOffDto>())!;
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);

        var first = removeWeeklyFirst ? weekly : specific;
        var remaining = removeWeeklyFirst ? specific : weekly;
        using var firstDeleted = await employee.DeleteAsync($"{daysUrl}/{first.Id}");
        Assert.Equal(HttpStatusCode.NoContent, firstDeleted.StatusCode);
        Assert.Equal(remaining, Assert.Single((await employee.GetFromJsonAsync<DayOffDto[]>(daysUrl))!));
        Assert.Empty((await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
        var followingTuesday = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url("2026-11-17")))!;
        if (removeWeeklyFirst) Assert.NotEmpty(followingTuesday);
        else Assert.Empty(followingTuesday);

        using var secondDeleted = await employee.DeleteAsync($"{daysUrl}/{remaining.Id}");
        Assert.Equal(HttpStatusCode.NoContent, secondDeleted.StatusCode);
        Assert.Empty((await employee.GetFromJsonAsync<DayOffDto[]>(daysUrl))!);
        Assert.Equal(before, (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
        Assert.NotEmpty((await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url("2026-11-17")))!);
    }

    [Fact]
    public async Task DifferentOfferingsForOneEmployeeShareTheSameOccupiedCalendar()
    {
        await using var setup = await SeedAsync(30);
        Guid longerOfferingId;
        await using (var db = fixture.CreateContext())
        {
            var service = new Service("Long appointment");
            var offering = new StaffService(setup.Staff.Id, service.Id, 180m, 45);
            db.AddRange(service, offering);
            await db.SaveChangesAsync();
            longerOfferingId = offering.Id;
        }
        using var customer = setup.Client();
        var longerUrl = setup.Url().Replace(setup.Offering.Id.ToString(), longerOfferingId.ToString());
        var shorterBefore = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var longerBefore = (await customer.GetFromJsonAsync<AvailableSlot[]>(longerUrl))!;
        var selected = longerBefore[2];
        using var response = await customer.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(
            Guid.NewGuid(), setup.ClientUser.Id, longerOfferingId, selected.StartsAtUtc.UtcDateTime, 180m, 45));
        response.EnsureSuccessStatusCode();
        var booking = (await response.Content.ReadFromJsonAsync<BookingDto>())!;

        bool DoesNotOverlap(AvailableSlot slot) =>
            slot.EndsAtUtc <= selected.StartsAtUtc || slot.StartsAtUtc >= selected.EndsAtUtc;
        var shorterDuring = (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!;
        var longerDuring = (await customer.GetFromJsonAsync<AvailableSlot[]>(longerUrl))!;
        Assert.Equal(shorterBefore.Where(DoesNotOverlap), shorterDuring);
        Assert.Equal(longerBefore.Where(DoesNotOverlap), longerDuring);
        Assert.Contains(shorterDuring, s => s.EndsAtUtc == selected.StartsAtUtc);
        Assert.Contains(shorterDuring, s => s.StartsAtUtc == selected.EndsAtUtc);
        await using (var scope = setup.Host.Services.CreateAsyncScope())
        {
            var availability = scope.ServiceProvider.GetRequiredService<AvailabilityService>();
            Assert.False(await availability.IsAvailableAsync(setup.Staff.Id, setup.Offering.Id,
                selected.StartsAtUtc, default));
            Assert.True(await availability.IsAvailableAsync(setup.Staff.Id, setup.Offering.Id,
                selected.EndsAtUtc, default));
            await scope.ServiceProvider.GetRequiredService<BookingService>()
                .CancelAsync(new Actor(setup.ClientUser.Id), booking.Id, default);
        }
        Assert.Equal(shorterBefore, (await customer.GetFromJsonAsync<AvailableSlot[]>(setup.Url()))!);
        Assert.Equal(longerBefore, (await customer.GetFromJsonAsync<AvailableSlot[]>(longerUrl))!);
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
