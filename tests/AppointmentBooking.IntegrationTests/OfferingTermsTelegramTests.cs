using System.Net;
using System.Net.Http.Json;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingTermsTelegramTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EmployeeCreatesAnOfferingAndTheSameTermsAppearThroughHttp()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        await bot.Click("Create offering", setup.Employee.TelegramUserId);
        await FindService(bot, service.Name, setup.Employee.TelegramUserId);
        await bot.Click(service.Name, setup.Employee.TelegramUserId);
        await bot.Send("0 20", setup.Employee.TelegramUserId);
        Assert.Contains("Offering created", bot.Sender.SentMenus.Last().Text);
        using var client = setup.Client();
        var saved = Assert.Single((await client.GetFromJsonAsync<ManagedOfferingDto[]>(setup.Url))!, o => o.ServiceId == service.Id);
        Assert.Equal(0m, saved.Price);
        Assert.Equal(20, saved.DurationMinutes);
        Assert.True(saved.IsActive);
        var count = bot.Sender.SentMenus.Count;
        await bot.Send("100 30", setup.Employee.TelegramUserId);
        Assert.Equal(count, bot.Sender.SentMenus.Count);
        await bot.Click("View offerings", setup.Employee.TelegramUserId);
        Assert.Contains(service.Name, bot.Sender.SentMenus.Last().Text);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.StaffServices.Where(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == service.Id).ToListAsync());
    }

    [Theory]
    [InlineData("1.001 30")]
    [InlineData("10000000000 30")]
    [InlineData("-1 30")]
    [InlineData("1,25 30")]
    [InlineData("25 0")]
    [InlineData("25 1.5")]
    [InlineData("25")]
    public async Task InvalidTermsLeaveTheFormUsableWithoutChangingStoredValues(string input)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        await bot.Click("Edit " + setup.ActiveService.Name, setup.Employee.TelegramUserId);
        await bot.Send(input, setup.Employee.TelegramUserId);
        Assert.Contains("price and duration", bot.Sender.SentMenus.Last().Text);
        await using var db = fixture.CreateContext();
        var before = await db.StaffServices.AsNoTracking().SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(12.50m, before.Price);
        Assert.Equal(30, before.DurationMinutes);
        await bot.Send("5.2500 55", setup.Employee.TelegramUserId);
        Assert.Contains("Offering updated", bot.Sender.SentMenus.Last().Text);
        var after = await db.StaffServices.AsNoTracking().SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(5.25m, after.Price);
        Assert.Equal(55, after.DurationMinutes);
    }

    [Fact]
    public async Task EditingDisabledOfferingPreservesStatusAndBackRestoresOverview()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        await using var names = fixture.CreateContext();
        var name = (await names.Services.SingleAsync(s => s.Id == setup.Disabled.ServiceId)).Name;
        var oldEdit = bot.Button("Edit " + name);
        await bot.Deliver(bot.Callback(oldEdit, setup.Employee.TelegramUserId));
        await bot.Send("75 90", setup.Employee.TelegramUserId);
        await bot.Click("Back", setup.Employee.TelegramUserId);
        Assert.Contains("Employee offerings", bot.Sender.SentMenus.Last().Text);
        await using var db = fixture.CreateContext();
        var saved = await db.StaffServices.SingleAsync(o => o.Id == setup.Disabled.Id);
        Assert.False(saved.IsActive);
        Assert.Equal(75m, saved.Price);
        Assert.Equal(90, saved.DurationMinutes);
        await bot.Deliver(bot.Callback(oldEdit, setup.Employee.TelegramUserId));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSuccessReplyDoesNotReplayAWriteAfterNewerHttpTerms(bool create)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = create ? await NewService() : setup.ActiveService;
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        if (create)
        {
            await bot.Click("Create offering", setup.Employee.TelegramUserId);
            await FindService(bot, service.Name, setup.Employee.TelegramUserId);
            await bot.Click(service.Name, setup.Employee.TelegramUserId);
        }
        else await bot.Click("Edit " + service.Name, setup.Employee.TelegramUserId);
        var delivery = bot.Message("25 40", setup.Employee.TelegramUserId);
        bot.Sender.FailNextSend(new IOException("Reply failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(delivery));
        using var client = setup.Client();
        var original = Assert.Single((await client.GetFromJsonAsync<ManagedOfferingDto[]>(setup.Url))!, o => o.ServiceId == service.Id);
        Assert.Equal(25m, original.Price);
        Assert.Equal(40, original.DurationMinutes);
        using var updated = await client.PutAsJsonAsync($"/api/staff/{setup.Staff.Id}/services/{service.Id}/terms",
            new { price = 80m, durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        bot.Sender.FailNextSend(new IOException("Second reply failure"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(delivery));
        await bot.Deliver(delivery);
        Assert.Contains(create ? "Offering created" : "Offering updated", bot.Sender.SentMenus.Last().Text);
        var view = bot.Callback(bot.Button("View offerings"), setup.Employee.TelegramUserId);
        bot.Sender.FailNextSend(new IOException("Return to overview failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(view));
        await bot.Deliver(view);
        Assert.Contains("80.00", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", setup.Employee.TelegramUserId);
        Assert.Contains("Choose an employee", bot.Sender.SentMenus.Last().Text);
        await using var db = fixture.CreateContext();
        var saved = Assert.Single(await db.StaffServices.Where(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == service.Id).ToListAsync());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(80m, saved.Price);
        Assert.Equal(60, saved.DurationMinutes);
    }

    [Fact]
    public async Task CancelAndForeignCallbacksDoNotCreateOrEditOfferings()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        var edit = bot.Button("Edit " + setup.ActiveService.Name);
        await bot.Deliver(bot.Callback(edit, setup.Customer.TelegramUserId));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Click("Edit " + setup.ActiveService.Name, setup.Employee.TelegramUserId);
        await bot.Click("Cancel", setup.Employee.TelegramUserId);
        await bot.Send("100 60", setup.Employee.TelegramUserId);
        await bot.Deliver(bot.Callback(edit, setup.Employee.TelegramUserId));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Send("/off.edit", setup.Customer.TelegramUserId);
        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
        Assert.Equal(12.50m, (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Fact]
    public async Task DeactivationAfterOpeningTheFormDeniesTheSave()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        await bot.Click("Edit " + setup.ActiveService.Name, setup.Employee.TelegramUserId);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await bot.Send("100 60", setup.Employee.TelegramUserId);
        Assert.Contains("permission", bot.Sender.SentMessages.Last().Text);
        await using var verify = fixture.CreateContext();
        Assert.Equal(12.50m, (await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Fact]
    public async Task ConcurrentHttpCreationIsReportedAsDuplicateWithoutOverwritingItsTerms()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        await bot.Click("Create offering", setup.Employee.TelegramUserId);
        await FindService(bot, service.Name, setup.Employee.TelegramUserId);
        await bot.Click(service.Name, setup.Employee.TelegramUserId);
        using var client = setup.Client();
        using var created = await client.PostAsJsonAsync($"/api/staff/{setup.Staff.Id}/services",
            new { serviceId = service.Id, price = 45m, durationMinutes = 75 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await bot.Send("10 30", setup.Employee.TelegramUserId);
        Assert.Contains("already has an offering", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", setup.Employee.TelegramUserId);
        Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, b => b.Text == service.Name);
        await using var db = fixture.CreateContext();
        var saved = Assert.Single(await db.StaffServices.Where(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == service.Id).ToListAsync());
        Assert.Equal(45m, saved.Price);
        Assert.Equal(75, saved.DurationMinutes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryingFailedFormPromptRestoresOneFormWithoutWriting(bool create)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await Open(bot, setup);
        string button;
        if (create)
        {
            var service = await NewService();
            await bot.Click("Create offering", setup.Employee.TelegramUserId);
            await FindService(bot, service.Name, setup.Employee.TelegramUserId);
            button = bot.Button(service.Name);
        }
        else button = bot.Button("Edit " + setup.ActiveService.Name);
        var delivery = bot.Callback(button, setup.Employee.TelegramUserId);
        bot.Sender.FailNextSend(new IOException("Form prompt failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(delivery));
        await bot.Deliver(delivery);
        Assert.Contains("price and duration", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", setup.Employee.TelegramUserId);
        Assert.Contains(create ? "Choose a shared service" : "Employee offerings", bot.Sender.SentMenus.Last().Text);
        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
        Assert.Equal(12.50m, (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Fact]
    public async Task FailedServicePageReplyDoesNotAdvanceTwiceOrInvalidateRecoveredNavigation()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using (var db = fixture.CreateContext())
        {
            db.Services.AddRange(Enumerable.Range(0, 15).Select(i => new Service($"!! Terms page {i:D2} " + Guid.NewGuid())));
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        await Open(bot, setup);
        await bot.Click("Create offering", user);
        var first = bot.Sender.SentMenus.Last().Buttons.Select(b => b.Text).ToArray();
        var oldSelection = bot.Sender.SentMenus.Last().Buttons.First(b => b.Text.StartsWith("!! Terms page")).CallbackData;
        var delivery = bot.Callback(bot.Button("Next services"), user);
        bot.Sender.FailNextSend(new IOException("Next page failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(delivery));
        await bot.Deliver(delivery);
        Assert.Contains("page 2 of", bot.Sender.SentMenus.Last().Text);
        await bot.Deliver(bot.Callback(oldSelection, user));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Click("Previous services", user);
        Assert.Equal(first, bot.Sender.SentMenus.Last().Buttons.Select(b => b.Text));
        await bot.Click("Back", user);
        Assert.Contains("Employee offerings", bot.Sender.SentMenus.Last().Text);
    }

    [Fact]
    public async Task OwnerCanEditInactiveEmployeesTermsWithoutReactivatingThem()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Owner.TelegramUserId;
        await bot.Send("/offerings", user);
        await FindService(bot, setup.Staff.DisplayName + " (inactive)", user, "Next employees");
        await bot.Click(setup.Staff.DisplayName + " (inactive)", user);
        await bot.Click("Edit " + setup.ActiveService.Name, user);
        await bot.Send("90 75", user);
        Assert.Contains("Offering updated", bot.Sender.SentMenus.Last().Text);
        await using var verify = fixture.CreateContext();
        Assert.False((await verify.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).IsActive);
        var saved = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(90m, saved.Price);
        Assert.Equal(75, saved.DurationMinutes);
    }

    [Fact]
    public async Task ServiceDeactivatedAfterSelectionRejectsTelegramCreationWithoutAddingAnOffering()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var service = await NewService();
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        await Open(bot, setup);
        await bot.Click("Create offering", user);
        await FindService(bot, service.Name, user);
        await bot.Click(service.Name, user);
        await using (var db = fixture.CreateContext())
        {
            (await db.Services.SingleAsync(s => s.Id == service.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await bot.Send("25 30", user);
        Assert.Contains("inactive", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", user);
        Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, b => b.Text == service.Name);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.StaffServices.AnyAsync(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == service.Id));
    }

    private static async Task Open(TelegramManagementBot bot, OfferingOverviewScenario setup)
    {
        await bot.Send("/offerings", setup.Employee.TelegramUserId);
        await bot.Click(setup.Staff.DisplayName, setup.Employee.TelegramUserId);
    }

    private static async Task FindService(TelegramManagementBot bot, string name, long user, string next = "Next services")
    {
        for (var page = 0; page < 100; page++)
        {
            if (bot.Sender.SentMenus.Last().Buttons.Any(b => b.Text == name)) return;
            await bot.Click(next, user);
        }
        Assert.Fail("Shared service was not found in the selection list.");
    }

    private async Task<Service> NewService()
    {
        await using var db = fixture.CreateContext();
        var service = new Service("Terms Telegram " + Guid.NewGuid());
        db.Services.Add(service);
        await db.SaveChangesAsync();
        return service;
    }
}
