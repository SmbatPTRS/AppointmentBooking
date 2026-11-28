using System.Net;
using System.Net.Http.Json;
using AppointmentBooking.IntegrationTests.Fixtures;
using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogActivationTelegramTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationRequiresAnExplicitClickAndFailedPromptDoesNotSubmit(bool shared)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = shared ? setup.Owner.TelegramUserId : setup.Employee.TelegramUserId;
        await Open(bot, setup, shared, user);
        var selection = bot.Callback(bot.Button("Deactivate " + setup.ActiveService.Name), user);
        bot.Sender.FailNextSend(new IOException("Confirmation prompt failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(selection));
        await bot.Deliver(selection);
        await bot.Send("yes", user);
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
        Assert.True((await verify.StaffServices.SingleAsync(s => s.Id == setup.Active.Id)).IsActive);
        Assert.Equal(0L, (await verify.BookingCoordinationRows.SingleAsync(r => r.StaffMemberId == setup.Staff.Id)).ChangeVersion);
        await bot.Click("Back", user);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "Deactivate " + setup.ActiveService.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedReplyRetryCannotUndoANewerOppositeHttpActivation(bool shared)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        if (shared)
        {
            await using var seed = fixture.CreateContext();
            seed.Services.AddRange(Enumerable.Range(0, 10).Select(i => new Service($"000 activation page {i} {Guid.NewGuid()}")));
            await seed.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = shared ? setup.Owner.TelegramUserId : setup.Employee.TelegramUserId;
        await Open(bot, setup, shared, user);
        await bot.Click("Deactivate " + setup.ActiveService.Name, user);
        var delivery = bot.Callback(bot.Button("Confirm deactivation"), user);
        bot.Sender.FailNextSend(new IOException("Success reply failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(delivery));
        using var owner = setup.Client("owner");
        using var changed = await owner.PutAsJsonAsync(Url(setup, shared), new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        bot.Sender.FailNextSend(new IOException("Retry reply failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(delivery));
        await bot.Deliver(delivery);
        Assert.Contains("deactivated", bot.Sender.SentMenus.Last().Text);
        await bot.Click(shared ? "View services" : "View offerings", user);
        if (shared) await FindServicePage(bot, setup, user);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "Deactivate " + setup.ActiveService.Name);
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
        Assert.True((await verify.StaffServices.SingleAsync(s => s.Id == setup.Active.Id)).IsActive);
        Assert.Equal(1L, (await verify.BookingCoordinationRows.SingleAsync(r => r.StaffMemberId == setup.Staff.Id)).ChangeVersion);
        await bot.Click("Back", user);
        Assert.Contains(shared ? "Your Telegram user ID" : "Choose an employee", bot.Sender.SentMenus.Last().Text);
    }

    [Fact]
    public async Task ConfirmationStoresTheTargetInsteadOfInvertingCurrentStatus()
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        await Open(bot, setup, false, user);
        await bot.Click("Deactivate " + setup.ActiveService.Name, user);
        using var owner = setup.Client("owner");
        using var changed = await owner.PutAsJsonAsync(Url(setup, false), new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await bot.Click("Confirm deactivation", user);
        await using var verify = fixture.CreateContext();
        Assert.False((await verify.StaffServices.SingleAsync(s => s.Id == setup.Active.Id)).IsActive);
        Assert.Equal(1L, (await verify.BookingCoordinationRows.SingleAsync(r => r.StaffMemberId == setup.Staff.Id)).ChangeVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignAndCancelledConfirmationButtonsCannotChangeStatus(bool shared)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = shared ? setup.Owner.TelegramUserId : setup.Employee.TelegramUserId;
        await Open(bot, setup, shared, user);
        await bot.Click("Deactivate " + setup.ActiveService.Name, user);
        var confirm = bot.Button("Confirm deactivation");
        await bot.Deliver(bot.Callback(confirm, setup.Customer.TelegramUserId));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Click("Cancel", user);
        await bot.Deliver(bot.Callback(confirm, user));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
        Assert.True((await verify.StaffServices.SingleAsync(s => s.Id == setup.Active.Id)).IsActive);
        Assert.Equal(0L, (await verify.BookingCoordinationRows.SingleAsync(r => r.StaffMemberId == setup.Staff.Id)).ChangeVersion);
    }

    private static string Url(OfferingOverviewScenario setup, bool shared) => shared
        ? $"/api/services/{setup.ActiveService.Id}/activation"
        : $"/api/staff/{setup.Staff.Id}/services/{setup.ActiveService.Id}/activation";

    private static async Task Open(TelegramManagementBot bot, OfferingOverviewScenario setup, bool shared, long user)
    {
        await bot.Send(shared ? "/services" : "/offerings", user);
        if (!shared) await bot.Click(setup.Staff.DisplayName, user);
        else await FindServicePage(bot, setup, user);
    }

    private static async Task FindServicePage(TelegramManagementBot bot, OfferingOverviewScenario setup, long user)
    {
        for (var page = 0; page < 100; page++)
        {
            if (bot.Sender.SentMenus.Last().Buttons.Any(b => b.Text == "Deactivate " + setup.ActiveService.Name)) return;
            await bot.Click("Next page", user);
        }
        Assert.Fail("Target service was not found.");
    }
}
