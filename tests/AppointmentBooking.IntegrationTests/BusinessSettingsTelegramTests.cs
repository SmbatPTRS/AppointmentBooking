using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

public sealed class BusinessSettingsTelegramTests
{
    [Fact]
    public async Task OwnerEditsSettingsWithRecoveryAndBookingHistoryLocksRegionalFields()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        const long ownerId = 9_056_000_001;
        const long employeeId = 9_056_000_002;
        const long clientId = 9_056_000_003;
        Guid ownerUserId;
        Guid staffId;
        await using (var db = fixture.CreateContext())
        {
            await new DatabaseSeeder(db).SeedBusinessSettingsAsync(new BusinessSettings("Business", "Employee", "UTC", "AMD"));
            var owner = new User(ownerId, "Owner", null, DateTime.UtcNow);
            var employee = new User(employeeId, "Employee", null, DateTime.UtcNow);
            var profile = new StaffMember(employee.Id, "Employee");
            ownerUserId = owner.Id;
            staffId = profile.Id;
            db.AddRange(owner, employee, profile);
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId);
        await bot.Send("/start", ownerId);
        Assert.Contains($"Your Telegram user ID is {ownerId}", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Business settings", ownerId);
        Assert.Contains("Name — Business", bot.Sender.SentMenus.Last().Text);
        var oldLabels = bot.Button("Edit business name and staff label");
        await bot.Click("Edit business name and staff label", ownerId);
        await bot.Send("one line", ownerId);
        Assert.Contains("exactly two lines", bot.Sender.SentMenus.Last().Text);
        await bot.Send(" \nEmployee", ownerId);
        Assert.Contains("must contain", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", ownerId);
        Assert.Contains("Name — Business", bot.Sender.SentMenus.Last().Text);
        var menuCount = bot.Sender.SentMenus.Count;
        await bot.Deliver(bot.Callback(oldLabels, ownerId));
        Assert.Equal(menuCount, bot.Sender.SentMenus.Count);
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);

        await bot.Click("Edit business name and staff label", ownerId);
        var labels = bot.Message(" Studio \r\n Specialist ", ownerId);
        bot.Sender.FailNextSend(new HttpRequestException("Label result failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Deliver(labels));
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.BusinessSettings.SingleAsync();
            Assert.Equal("Studio", saved.DisplayName);
            saved.Update("Later studio", "Specialist", saved.TimeZoneId, saved.CurrencyCode);
            await db.SaveChangesAsync();
        }
        await bot.Deliver(labels);
        Assert.Contains("Name — Studio", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", ownerId);
        Assert.Contains("Name — Later studio", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Edit timezone and currency", ownerId);
        foreach (var invalid in new[] { "Invalid/Zone\nUSD", "Asia/Yerevan\nUS", "UTC\nU1D" })
        {
            await bot.Send(invalid, ownerId);
            Assert.Contains("Send both lines again", bot.Sender.SentMenus.Last().Text);
            await using var db = fixture.CreateContext();
            var unchanged = await db.BusinessSettings.SingleAsync();
            Assert.Equal("UTC", unchanged.TimeZoneId);
            Assert.Equal("AMD", unchanged.CurrencyCode);
        }
        var region = bot.Message("Asia/Yerevan\nusd", ownerId);
        bot.Sender.FailNextSend(new HttpRequestException("Regional result failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Deliver(region));
        long version;
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.BusinessSettings.SingleAsync();
            Assert.Equal("Asia/Yerevan", saved.TimeZoneId);
            Assert.Equal("USD", saved.CurrencyCode);
            version = (await db.BookingCoordinationRows.SingleAsync()).ChangeVersion;
        }
        await bot.Deliver(region);
        await using (var db = fixture.CreateContext())
            Assert.Equal(version, (await db.BookingCoordinationRows.SingleAsync()).ChangeVersion);
        Assert.Contains("Currency — USD", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", ownerId);
        await bot.Click("Edit timezone and currency", ownerId);
        await bot.Click("Cancel", ownerId);
        Assert.Contains("Welcome", bot.Sender.SentMenus.Last().Text);

        await using (var db = fixture.CreateContext())
        {
            var service = new Service("Consultation");
            var offering = new StaffService(staffId, service.Id, 25m, 30);
            var now = DateTime.UtcNow;
            var booking = new Booking(ownerUserId, staffId, offering.Id, Guid.NewGuid(), now.AddHours(1), 25m, 30, now);
            booking.Cancel(now);
            db.AddRange(service, offering, booking);
            await db.SaveChangesAsync();
        }
        await bot.Send("/settings", ownerId);
        await bot.Click("Edit timezone and currency", ownerId);
        await bot.Send("UTC\nAMD", ownerId);
        Assert.Contains("locked because a booking exists", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", ownerId);
        Assert.Contains("Timezone — Asia/Yerevan", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Edit business name and staff label", ownerId);
        await bot.Send("After booking\nTeam member", ownerId);
        Assert.Contains("Business labels updated", bot.Sender.SentMenus.Last().Text);

        foreach (var userId in new[] { employeeId, clientId })
        {
            await bot.Send("/start", userId);
            Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, button => button.Text == "Business settings");
            foreach (var command in new[] { "/settings", "/settings.labels", "/settings.region" })
            {
                menuCount = bot.Sender.SentMenus.Count;
                await bot.Send(command, userId);
                Assert.Equal(menuCount, bot.Sender.SentMenus.Count);
                Assert.Contains("permission", bot.Sender.SentMessages.Last().Text);
            }
        }
        await using (var db = fixture.CreateContext())
        {
            var current = await db.BusinessSettings.SingleAsync();
            Assert.Equal("After booking", current.DisplayName);
            Assert.Equal("Team member", current.StaffLabel);
            Assert.Equal("Asia/Yerevan", current.TimeZoneId);
            Assert.Equal("USD", current.CurrencyCode);
        }
    }

    [Fact]
    public async Task MissingSettingsLeavesUsableNavigation()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, 9_056_000_011);
        await bot.Send("/settings", 9_056_000_011);
        Assert.Contains("not been initialized", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", 9_056_000_011);
        Assert.Contains("Welcome", bot.Sender.SentMenus.Last().Text);
    }
}
