using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduleEndpointsTests(PostgresFixture fixture)
{
    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed record Setup(ApiFactory Factory, WebApplicationFactory<Program> Host,
        User Owner, User Employee, StaffMember Staff, StaffMember Other) : IAsyncDisposable
    {
        public string Schedule => $"/api/staff/{Staff.Id}/schedule";
        public string Days => $"/api/staff/{Staff.Id}/days-off";
        public HttpClient Client(string? key = "staff-key")
        {
            var client = Host.CreateClient();
            if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
            return client;
        }
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); await Factory.DisposeAsync(); }
    }

    private async Task<Setup> SeedAsync(bool inactive = false)
    {
        await using var db = fixture.CreateContext();
        var clock = new Clock();
        User NewUser(string name) => new(Random.Shared.NextInt64(5000000000, 9000000000), name, null, clock.UtcNow);
        var owner = NewUser("Owner");
        var employee = NewUser("Employee");
        var otherUser = NewUser("Other");
        var staff = new StaffMember(employee.Id, "Employee");
        var other = new StaffMember(otherUser.Id, "Other");
        if (inactive) staff.Deactivate();
        db.AddRange(owner, employee, otherUser, staff, other, new BookingCoordination(staff), new BookingCoordination(other));
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null)
        {
            settings = new BusinessSettings("Studio", "Staff", "UTC", "AMD");
            db.Add(settings);
        }
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == settings.Id))
            db.Add(new BookingCoordination(settings));
        await db.SaveChangesAsync();
        var factory = new ApiFactory(fixture.ConnectionString);
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Owner:TelegramUserId"] = owner.TelegramUserId.ToString(),
                ["ApiAccess:Keys:0:Key"] = "owner-key",
                ["ApiAccess:Keys:0:TelegramUserId"] = owner.TelegramUserId.ToString(),
                ["ApiAccess:Keys:1:Key"] = "staff-key",
                ["ApiAccess:Keys:1:TelegramUserId"] = employee.TelegramUserId.ToString(),
                ["ApiAccess:Keys:2:Key"] = "other-key",
                ["ApiAccess:Keys:2:TelegramUserId"] = otherUser.TelegramUserId.ToString()
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(clock));
        });
        return new Setup(factory, host, owner, employee, staff, other);
    }

    private static readonly WorkingHoursModel Hours = new(new(9, 0), new(18, 0), new(12, 0), new(13, 0));
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string? code = null)
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
    [InlineData("invalid-key")]
    public async Task EveryRouteRequiresAuthentication(string? key)
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client(key);
        await Error(await client.GetAsync(setup.Schedule), HttpStatusCode.Unauthorized);
        await Error(await client.PutAsJsonAsync(setup.Schedule, Hours), HttpStatusCode.Unauthorized);
        await Error(await client.GetAsync(setup.Days), HttpStatusCode.Unauthorized);
        await Error(await client.PostAsJsonAsync(setup.Days, new DayOffModel(DayOfWeek.Sunday)), HttpStatusCode.Unauthorized);
        await Error(await client.DeleteAsync(setup.Days + "/" + Guid.NewGuid()), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("staff-key")]
    [InlineData("owner-key")]
    public async Task AuthorizedCallerCanConfigureHoursAndManageBothDayOffTypes(string key)
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client(key);
        using var missing = await client.GetAsync(setup.Schedule);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Equal("null", await missing.Content.ReadAsStringAsync());
        using var saved = await client.PutAsJsonAsync(setup.Schedule, Hours);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal(Hours, await client.GetFromJsonAsync<WorkingHoursModel>(setup.Schedule));
        var noBreak = Hours with { BreakStartsAt = null, BreakEndsAt = null };
        using var updated = await client.PutAsJsonAsync(setup.Schedule, noBreak);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.Equal(noBreak, await client.GetFromJsonAsync<WorkingHoursModel>(setup.Schedule));
        foreach (var day in new[] { new DayOffModel(DayOfWeek.Sunday), new DayOffModel(Date: new(2027, 2, 1)) })
        {
            using var added = await client.PostAsJsonAsync(setup.Days, day);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var dto = (await added.Content.ReadFromJsonAsync<DayOffDto>())!;
            Assert.Equal(day.Weekday, dto.Weekday);
            Assert.Equal(day.Date, dto.Date);
            Assert.Contains(dto, (await client.GetFromJsonAsync<DayOffDto[]>(setup.Days))!);
            await Error(await client.PostAsJsonAsync(setup.Days, day), HttpStatusCode.Conflict, ErrorCodes.DuplicateDayOff);
            using var removed = await client.DeleteAsync(setup.Days + "/" + dto.Id);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            await Error(await client.DeleteAsync(setup.Days + "/" + dto.Id), HttpStatusCode.NotFound, ErrorCodes.DayOffNotFound);
        }
        Assert.Empty((await client.GetFromJsonAsync<DayOffDto[]>(setup.Days))!);
    }

    [Theory]
    [InlineData(false, "other-key", "access.forbidden")]
    [InlineData(true, "staff-key", "staff.inactive")]
    public async Task ForbiddenCallerCannotReadOrMutateSchedule(bool inactive, string key, string code)
    {
        await using var setup = await SeedAsync(inactive);
        using var client = setup.Client(key);
        await Error(await client.GetAsync(setup.Schedule), HttpStatusCode.Forbidden, code);
        await Error(await client.PutAsJsonAsync(setup.Schedule, Hours), HttpStatusCode.Forbidden, code);
        await Error(await client.GetAsync(setup.Days), HttpStatusCode.Forbidden, code);
        await Error(await client.PostAsJsonAsync(setup.Days, new DayOffModel(DayOfWeek.Sunday)), HttpStatusCode.Forbidden, code);
        await Error(await client.DeleteAsync(setup.Days + "/" + Guid.NewGuid()), HttpStatusCode.Forbidden, code);
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == setup.Staff.Id));
        Assert.False(await db.StaffDaysOff.AnyAsync(d => d.StaffMemberId == setup.Staff.Id));
    }

    [Fact]
    public async Task OwnerCanManageInactiveStaffButCannotDeleteForeignDay()
    {
        await using var setup = await SeedAsync(true);
        using var client = setup.Client("owner-key");
        using var saved = await client.PutAsJsonAsync(setup.Schedule, Hours);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        await using var db = fixture.CreateContext();
        var foreign = new StaffDayOff(setup.Other.Id, DayOfWeek.Sunday, null);
        db.Add(foreign);
        await db.SaveChangesAsync();
        await Error(await client.DeleteAsync(setup.Days + "/" + foreign.Id), HttpStatusCode.NotFound, ErrorCodes.DayOffNotFound);
        Assert.True(await db.StaffDaysOff.AsNoTracking().AnyAsync(d => d.Id == foreign.Id));
    }

    [Fact]
    public async Task InvalidHoursDaysAndMissingStaffReturnSharedErrors()
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        await Error(await client.PutAsJsonAsync(setup.Schedule, Hours with { ClosesAt = new(8, 0) }), HttpStatusCode.BadRequest, ErrorCodes.InvalidWorkingHours);
        await Error(await client.PostAsJsonAsync(setup.Days, new DayOffModel()), HttpStatusCode.BadRequest, ErrorCodes.InvalidDayOff);
        await Error(await client.GetAsync($"/api/staff/{Guid.NewGuid()}/schedule"), HttpStatusCode.NotFound, ErrorCodes.StaffNotFound);
        await Error(await client.PutAsJsonAsync($"/api/staff/{Guid.Empty}/schedule", Hours), HttpStatusCode.NotFound, ErrorCodes.StaffNotFound);
    }
    [Fact]
    public async Task BookingCreatedThroughHttpPreventsConflictingScheduleChanges()
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        using var hours = await client.PutAsJsonAsync(setup.Schedule, Hours);
        hours.EnsureSuccessStatusCode();
        await using var db = fixture.CreateContext();
        var service = new Service("HTTP conflict");
        var offering = new StaffService(setup.Staff.Id, service.Id, 100m, 30);
        db.AddRange(service, offering);
        await db.SaveChangesAsync();
        var business = await db.BusinessSettings.SingleAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId);
        var date = new DateOnly(2026, 11, 10);
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(16, 0)), zone);
        var request = new AppointmentBooking.Application.Bookings.CreateBookingRequest(
            Guid.NewGuid(), setup.Employee.Id, offering.Id, start, 100m, 30);
        using var booking = await client.PostAsJsonAsync("/api/bookings", request);
        booking.EnsureSuccessStatusCode();
        await Error(await client.PutAsJsonAsync(setup.Schedule, Hours with { ClosesAt = new(16, 0) }),
            HttpStatusCode.Conflict, ErrorCodes.ScheduleConflictsWithBookings);
        await Error(await client.PutAsJsonAsync(setup.Schedule, Hours with { BreakStartsAt = new(16, 0), BreakEndsAt = new(17, 0) }),
            HttpStatusCode.Conflict, ErrorCodes.ScheduleConflictsWithBookings);
        foreach (var day in new[] { new DayOffModel(Date: date), new DayOffModel(date.DayOfWeek) })
            await Error(await client.PostAsJsonAsync(setup.Days, day), HttpStatusCode.Conflict, ErrorCodes.ScheduleConflictsWithBookings);
        Assert.Equal(Hours, await client.GetFromJsonAsync<WorkingHoursModel>(setup.Schedule));
        Assert.Empty((await client.GetFromJsonAsync<DayOffDto[]>(setup.Days))!);
        Assert.True(await db.Bookings.AnyAsync(b => b.RequestId == request.RequestId));
        using var touching = await client.PutAsJsonAsync(setup.Schedule, Hours with { ClosesAt = new(16, 30) });
        Assert.Equal(HttpStatusCode.NoContent, touching.StatusCode);
    }

    [Fact]
    public async Task SubmittedIdentityCannotOverrideAuthenticatedEmployee()
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client("other-key");
        using var response = await client.PutAsJsonAsync(setup.Schedule + "?userId=" + setup.Owner.Id, new
        {
            opensAt = "09:00:00", closesAt = "18:00:00", userId = setup.Owner.Id,
            actor = new { userId = setup.Owner.Id }, staffMemberId = setup.Other.Id
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == setup.Staff.Id));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"opensAt\":\"not-a-time\",\"closesAt\":\"18:00:00\"}")]
    [InlineData("{")]
    public async Task MalformedScheduleBodyReturnsBadRequest(string body)
    {
        await using var setup = await SeedAsync();
        using var client = setup.Client();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(setup.Schedule, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkingHours.AnyAsync(h => h.StaffMemberId == setup.Staff.Id));
    }
}
