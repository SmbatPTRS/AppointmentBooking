using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AppointmentBooking.IntegrationTests;

public sealed class BusinessSettingsEndpointsTests
{
    [Fact]
    public async Task SettingsRespectHttpPermissionsAndBookingHistory()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var owner = new User(42, "Owner", null, DateTime.UnixEpoch);
        var staff = new User(43, "Staff", null, DateTime.UnixEpoch);
        await using (var db = fixture.CreateContext())
        {
            await new DatabaseSeeder(db).SeedBusinessSettingsAsync(
                new BusinessSettings("Business", "Employee", "UTC", "AMD"));

            var client = new User(44, "Client", null, DateTime.UnixEpoch);
            db.AddRange(owner, staff, client, new StaffMember(staff.Id, "Staff"));
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Owner:TelegramUserId"] = "42",
                    ["ApiAccess:Keys:0:Key"] = "settings-owner-key",
                    ["ApiAccess:Keys:0:TelegramUserId"] = "42",
                    ["ApiAccess:Keys:1:Key"] = "settings-staff-key",
                    ["ApiAccess:Keys:1:TelegramUserId"] = "43",
                    ["ApiAccess:Keys:2:Key"] = "settings-client-key",
                    ["ApiAccess:Keys:2:TelegramUserId"] = "44"
                })));

        const string route = "/api/business-settings";
        const string regionalRoute = route + "/time-zone-and-currency";
        var regionalUpdate = new UpdateBusinessTimeZoneAndCurrencyRequest("Asia/Yerevan", "usd");
        var original = new BusinessSettingsView("Business", "Employee", "UTC", "AMD");
        var update = new UpdateBusinessLabelsRequest("Studio", "Specialist");

        using var anonymous = host.CreateClient();
        using var anonymousRead = await anonymous.GetAsync(route);
        using var anonymousUpdate = await anonymous.PutAsJsonAsync(route, update);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUpdate.StatusCode);

        using var anonymousRegionalUpdate = await anonymous.PutAsJsonAsync(regionalRoute, regionalUpdate);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRegionalUpdate.StatusCode);

        foreach (string key in new[] { "settings-staff-key", "settings-client-key" })
        {
            using var reader = host.CreateClient();
            reader.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
            Assert.Equal(original, await reader.GetFromJsonAsync<BusinessSettingsView>(route));

            using var forbidden = await reader.PutAsJsonAsync(route, update);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var forbiddenRegionalUpdate = await reader.PutAsJsonAsync(regionalRoute, regionalUpdate);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRegionalUpdate.StatusCode);
        }

        using var editor = host.CreateClient();
        editor.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "settings-owner-key");
        Assert.Equal(original, await editor.GetFromJsonAsync<BusinessSettingsView>(route));

        using var invalid = await editor.PutAsJsonAsync(route, update with { StaffLabel = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var unsupported = await editor.PutAsJsonAsync(route,
            new { update.DisplayName, update.StaffLabel, TimeZoneId = "Asia/Yerevan" });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        using var updated = await editor.PutAsJsonAsync(route, update);
        updated.EnsureSuccessStatusCode();
        var expected = original with { DisplayName = "Studio", StaffLabel = "Specialist" };
        Assert.Equal(expected, await updated.Content.ReadFromJsonAsync<BusinessSettingsView>());
        using var invalidZone = await editor.PutAsJsonAsync(
            regionalRoute, regionalUpdate with { TimeZoneId = "Invalid/Zone" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);

        using var invalidCurrency = await editor.PutAsJsonAsync(
            regionalRoute, regionalUpdate with { CurrencyCode = "US" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCurrency.StatusCode);
        Assert.Equal(expected, await editor.GetFromJsonAsync<BusinessSettingsView>(route));

        using var updatedRegion = await editor.PutAsJsonAsync(regionalRoute, regionalUpdate);
        updatedRegion.EnsureSuccessStatusCode();
        expected = expected with { TimeZoneId = "Asia/Yerevan", CurrencyCode = "USD" };
        Assert.Equal(expected, await updatedRegion.Content.ReadFromJsonAsync<BusinessSettingsView>());

        await using (var db = fixture.CreateContext())
        {
            var member = await db.StaffMembers.SingleAsync();
            var service = new Service("Consultation");
            var offering = new StaffService(member.Id, service.Id, 25m, 30);
            var now = DateTime.UtcNow;
            var booking = new Booking(owner.Id, member.Id, offering.Id, Guid.NewGuid(),
                now.AddHours(1), 25m, 30, now);
            booking.Cancel(now);
            db.AddRange(service, offering, booking);
            await db.SaveChangesAsync();
        }

        using var locked = await editor.PutAsJsonAsync(
            regionalRoute, new UpdateBusinessTimeZoneAndCurrencyRequest("UTC", "AMD"));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        using var problem = JsonDocument.Parse(await locked.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.BusinessSettingsLocked, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(expected, await editor.GetFromJsonAsync<BusinessSettingsView>(route));

        using var labelsAfterBooking = await editor.PutAsJsonAsync(route, update with { DisplayName = "New Studio" });
        labelsAfterBooking.EnsureSuccessStatusCode();
        Assert.Equal(expected with { DisplayName = "New Studio" },
            await labelsAfterBooking.Content.ReadFromJsonAsync<BusinessSettingsView>());
    }
}
