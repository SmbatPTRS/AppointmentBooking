using System.Net;
using System.Net.Http.Json;
using AppointmentBooking.Api.Authentication;
using AppointmentBooking.Api.Telegram.Handlers;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

public sealed class PermissionAcceptanceTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HttpUsesStoredIdentityAndRetainsInactiveAppointmentAccess()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var seed = await SeedAsync(fixture);
        await using var factory = new ApiFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Owner:TelegramUserId"] = "101",
                    ["ApiAccess:Keys:0:Key"] = "acceptance-owner",
                    ["ApiAccess:Keys:0:TelegramUserId"] = "101",
                    ["ApiAccess:Keys:1:Key"] = "acceptance-a",
                    ["ApiAccess:Keys:1:TelegramUserId"] = "102",
                    ["ApiAccess:Keys:2:Key"] = "acceptance-b",
                    ["ApiAccess:Keys:2:TelegramUserId"] = "103",
                    ["ApiAccess:Keys:3:Key"] = "acceptance-client",
                    ["ApiAccess:Keys:3:TelegramUserId"] = "104",
                    ["ApiAccess:Keys:4:Key"] = "acceptance-other-client",
                    ["ApiAccess:Keys:4:TelegramUserId"] = "105",
                    ["ApiAccess:Keys:5:Key"] = "acceptance-unregistered",
                    ["ApiAccess:Keys:5:TelegramUserId"] = "106"
                }));
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(new FixedClock()));
        });
        using var owner = Client("acceptance-owner");
        using var a = Client("acceptance-a");
        using var b = Client("acceptance-b");
        using var client = Client("acceptance-client");
        using var otherClient = Client("acceptance-other-client");
        foreach (var key in new string?[] { null, "wrong-key", "acceptance-unregistered" })
        {
            using var anonymous = Client(key);
            using var denied = await anonymous.GetAsync("/api/staff");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (var duplicate = Client("acceptance-owner"))
        {
            duplicate.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "acceptance-a");
            using var denied = await duplicate.GetAsync("/api/staff");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        // Spoofed headers/query IDs cannot replace the key's stored identity.
        a.DefaultRequestHeaders.Add("X-User-Id", seed.Owner.Id.ToString());
        a.DefaultRequestHeaders.Add("X-Telegram-User-Id", "101");
        using (var denied = await a.PatchAsJsonAsync($"/api/staff/{seed.B.Id}?userId={seed.Owner.Id}", new { displayName = "Stolen" }))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await b.PatchAsJsonAsync($"/api/staff/{seed.A.Id}", new { displayName = "Stolen" }))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var own = await a.PatchAsJsonAsync($"/api/staff/{seed.A.Id}", new { displayName = "Own A" }))
            own.EnsureSuccessStatusCode();
        using (var own = await b.PatchAsJsonAsync($"/api/staff/{seed.B.Id}", new { displayName = "Own B" }))
            own.EnsureSuccessStatusCode();
        using (var deactivated = await owner.PutAsJsonAsync($"/api/staff/{seed.A.Id}/activation", new { isActive = false }))
            deactivated.EnsureSuccessStatusCode();
        using (var denied = await a.PatchAsJsonAsync($"/api/staff/{seed.A.Id}", new { displayName = "After deactivation" }))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var appointments = $"/api/staff/{seed.A.Id}/bookings?dateFrom=2026-11-15&dateTo=2026-11-15";
        foreach (var allowed in new[] { owner, a })
        {
            var list = await allowed.GetFromJsonAsync<BookingDto[]>(appointments);
            Assert.Equal(seed.Booking.Id, Assert.Single(list!).Id);
        }
        foreach (var deniedCaller in new[] { b, client, otherClient })
        {
            using var denied = await deniedCaller.GetAsync(appointments);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedComplete = await deniedCaller.PostAsync($"/api/bookings/{seed.Booking.Id}/complete", null);
            Assert.Equal(HttpStatusCode.Forbidden, deniedComplete.StatusCode);
        }
        var mine = await client.GetFromJsonAsync<BookingDto[]>("/api/bookings/mine");
        Assert.Equal(seed.Booking.Id, Assert.Single(mine!).Id);
        Assert.Empty((await otherClient.GetFromJsonAsync<BookingDto[]>($"/api/bookings/mine?userId={seed.Client.Id}"))!);
        using (var complete = await a.PostAsync($"/api/bookings/{seed.Booking.Id}/complete", null))
            complete.EnsureSuccessStatusCode();
        using (var repeat = await owner.PostAsync($"/api/bookings/{seed.Booking.Id}/complete", null))
            repeat.EnsureSuccessStatusCode();
        // Authorization still applies after an idempotent transition.
        using (var denied = await b.PostAsync($"/api/bookings/{seed.Booking.Id}/complete", null))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(BookingStatus.Completed, (await verify.Bookings.SingleAsync()).Status);
        Assert.Equal("Own A", (await verify.StaffMembers.SingleAsync(row => row.Id == seed.A.Id)).DisplayName);
        Assert.Equal("Own B", (await verify.StaffMembers.SingleAsync(row => row.Id == seed.B.Id)).DisplayName);

        HttpClient Client(string? key)
        {
            var result = host.CreateClient();
            if (key is not null) result.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, key);
            return result;
        }
    }

    [Fact]
    public async Task TelegramUsesSenderInsteadOfChatAndRejectsRedirectedProfileState()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var seed = await SeedAsync(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, 101);
        // Messages in an owner's chat still act as their actual sender.
        var open = bot.Message("/profile", 102);
        open.Message!.Chat.Id = 101;
        await bot.Deliver(open);
        var rename = bot.Message("Own A in another chat", 102);
        rename.Message!.Chat.Id = 101;
        await bot.Deliver(rename);
        await bot.Send("/profile", 103);
        await bot.Send("Own B", 103);
        await bot.Send("/profile", 102);
        using (var lease = await bot.Services.GetRequiredService<ConversationStateStore>().AcquireAsync(new(102, 102)))
            lease.State.SetFeatureData("profile", new OwnProfileDraft(seed.B.Id));
        await bot.Send("Redirect attempt", 102);
        await bot.Send("/profile", 102);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(row => row.Id == seed.A.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await bot.Send("After deactivation", 102);
        Assert.Contains("active employee profile is required", bot.Sender.SentMenus.Last().Text);
        foreach (var caller in new long[] { 102, 103, 104, 105 })
        {
            await bot.Send("/staff", caller);
            Assert.Equal("You do not have permission for this action.", bot.Sender.SentMessages.Last().Text);
        }
        // Copy an owner's button into the owner's chat but change CallbackQuery.From.
        await bot.Send("/staff", 101);
        var copied = bot.Callback(bot.Button("Own B"), 103);
        copied.CallbackQuery!.Message!.Chat.Id = 101;
        copied.CallbackQuery.Message.From = new Telegram.Bot.Types.User { Id = 101, FirstName = "Owner" };
        var menuCount = bot.Sender.SentMenus.Count;
        await bot.Deliver(copied);
        Assert.Equal(menuCount, bot.Sender.SentMenus.Count);
        Assert.Contains("expired or changed", bot.Sender.CallbackAnswers.Last().Text!);
        await using var verify = fixture.CreateContext();
        Assert.Equal("Own A in another chat", (await verify.StaffMembers.SingleAsync(row => row.Id == seed.A.Id)).DisplayName);
        Assert.Equal("Own B", (await verify.StaffMembers.SingleAsync(row => row.Id == seed.B.Id)).DisplayName);
    }

    [Fact]
    public async Task InactiveEmployeeCanBrowseAndCompleteOwnAppointmentThroughTelegram()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var seed = await SeedAsync(fixture);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(row => row.Id == seed.A.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, 101,
            services => services.AddSingleton<IClock>(new FixedClock()));
        await bot.Send("/appointments", 102);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, button => button.Text == "Employee A");
        await bot.Click("Employee B", 102);
        await bot.Send("2026-11-15", 102);
        Assert.Equal("You do not have permission for this action.", bot.Sender.SentMessages.Last().Text);
        await bot.Send("/appointments", 102);
        await bot.Click("Employee A", 102);
        await bot.Send("2026-11-15", 102);
        Assert.Contains("Confirmed", bot.Sender.SentMenus.Last().Text);
        var complete = bot.Sender.SentMenus.Last().Buttons.Single(button => button.Text.StartsWith("Complete ")).CallbackData;
        var copied = bot.Callback(complete, 103);
        copied.CallbackQuery!.Message!.Chat.Id = 102;
        await bot.Deliver(copied);
        await using (var verify = fixture.CreateContext())
            Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.SingleAsync()).Status);
        await bot.Deliver(bot.Callback(complete, 102));
        Assert.Contains("Completed", bot.Sender.SentMenus.Last().Text);
        await bot.Send("/appointments", 101);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, button => button.Text == "Employee A");
        await bot.Click("Employee A", 101);
        await bot.Send("2026-11-15", 101);
        Assert.Contains("Completed", bot.Sender.SentMenus.Last().Text);
        foreach (var caller in new long[] { 104, 105 })
        {
            await bot.Send("/appointments", caller);
            Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, button => button.Text == "Employee A");
            await bot.Click("Employee B", caller);
            await bot.Send("2026-11-15", caller);
            Assert.Equal("You do not have permission for this action.", bot.Sender.SentMessages.Last().Text);
        }
        await using var final = fixture.CreateContext();
        Assert.Equal(BookingStatus.Completed, (await final.Bookings.SingleAsync()).Status);
    }

    private static async Task<Seed> SeedAsync(PostgresFixture fixture)
    {
        await using var db = fixture.CreateContext();
        await new DatabaseSeeder(db).SeedBusinessSettingsAsync(new BusinessSettings("Business", "Employee", "UTC", "AMD"));
        var owner = new User(101, "Owner", null, Now.AddDays(-1));
        var a = new User(102, "Employee A", null, Now.AddDays(-1));
        var b = new User(103, "Employee B", null, Now.AddDays(-1));
        var client = new User(104, "Client", null, Now.AddDays(-1));
        var otherClient = new User(105, "Other client", null, Now.AddDays(-1));
        var staffA = new StaffMember(a.Id, "Employee A");
        var staffB = new StaffMember(b.Id, "Employee B");
        var service = new Service("Consultation");
        var offering = new StaffService(staffA.Id, service.Id, 25m, 30);
        var booking = new Booking(client.Id, staffA.Id, offering.Id, Guid.NewGuid(), Now.AddHours(-2), 25m, 30, Now.AddDays(-1));
        db.AddRange(owner, a, b, client, otherClient, staffA, staffB, service, offering, booking,
            new BookingCoordination(staffA), new BookingCoordination(staffB));
        await db.SaveChangesAsync();
        return new Seed(owner, client, staffA, staffB, booking);
    }

    private sealed record Seed(User Owner, User Client, StaffMember A, StaffMember B, Booking Booking);
    private sealed class FixedClock : IClock { public DateTime UtcNow => Now; }
}
