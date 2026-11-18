using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingOverviewTelegramTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EmployeeSeesOwnCatalogueAndOrdinaryTextPreservesButtons()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        await bot.Send("/offerings", user);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == setup.Staff.DisplayName);
        Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, b => b.Text == setup.Other.DisplayName);
        await bot.Click(setup.Staff.DisplayName, user);
        var text = bot.Sender.SentMenus.Last().Text;
        Assert.Contains(setup.ActiveService.Name, text);
        Assert.Contains("Price: 12.50", text);
        Assert.Contains("Duration: 30 min", text);
        Assert.Contains("Offering: inactive", text);
        Assert.Contains("Service: inactive", text);
        var refresh = bot.Button("Refresh");
        var sent = bot.Sender.SentMenus.Count;
        await bot.Send("ordinary text", user);
        Assert.Equal(sent, bot.Sender.SentMenus.Count);
        Assert.Equal(refresh, bot.Button("Refresh"));
        await bot.Click("Back", user);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == setup.Staff.DisplayName);
        await bot.Click("Cancel", user);
        Assert.DoesNotContain("Employee offerings", bot.Sender.SentMenus.Last().Text);
    }

    [Fact]
    public async Task CustomersCannotEnterOrReuseAnEmployeesCallback()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await bot.Send("/offerings", setup.Customer.TelegramUserId);
        Assert.Contains("permission", bot.Sender.SentMessages.Last().Text);
        await bot.Send("/offerings", setup.Employee.TelegramUserId);
        var button = bot.Button(setup.Staff.DisplayName);
        await bot.Deliver(bot.Callback(button, setup.Customer.TelegramUserId));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Send("/off.staff", setup.Customer.TelegramUserId);
        Assert.DoesNotContain(setup.ActiveService.Name, bot.Sender.SentMessages.Last().Text);
    }

    [Fact]
    public async Task OwnerCanReadEmptyAndInactiveEmployees()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Other.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Owner.TelegramUserId;
        await bot.Send("/offerings", user);
        await FindEmployee(bot, setup.Other.DisplayName + " (inactive)", user);
        await bot.Click(setup.Other.DisplayName + " (inactive)", user);
        Assert.Contains("Employee: inactive", bot.Sender.SentMenus.Last().Text);
        Assert.Contains("No offerings configured", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", user);
        Assert.Contains("Choose an employee", bot.Sender.SentMenus.Last().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOpeningOrDetailsReplyCanBeRetriedWithoutLosingNavigation(bool details)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        var update = bot.Message("/offerings", user);
        if (details)
        {
            await bot.Deliver(update);
            update = bot.Callback(bot.Button(setup.Staff.DisplayName), user);
        }
        var before = bot.Sender.SentMenus.Count;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            bot.Sender.FailNextSend(new IOException("Send failed"));
            await Assert.ThrowsAsync<IOException>(() => bot.Deliver(update));
            Assert.Equal(before, bot.Sender.SentMenus.Count);
        }
        await bot.Deliver(update);
        Assert.Equal(before + 1, bot.Sender.SentMenus.Count);
        if (details)
        {
            Assert.Contains(setup.ActiveService.Name, bot.Sender.SentMenus.Last().Text);
            await bot.Click("Back", user);
            Assert.Contains("Choose an employee", bot.Sender.SentMenus.Last().Text);
        }
        else
            await bot.Click(setup.Staff.DisplayName, user);
        await bot.Click("Cancel", user);
    }

    [Fact]
    public async Task RefreshReadsChangedTermsAndRechecksEmployeeAccess()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        await bot.Send("/offerings", user);
        await bot.Click(setup.Staff.DisplayName, user);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).UpdateTerms(42.75m, 75);
            await db.SaveChangesAsync();
        }
        await bot.Click("Refresh", user);
        Assert.Contains("Price: 42.75", bot.Sender.SentMenus.Last().Text);
        Assert.Contains("Duration: 75 min", bot.Sender.SentMenus.Last().Text);
        var before = bot.Sender.SentMenus.Count;
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        await bot.Click("Refresh", user);
        Assert.Contains("permission", bot.Sender.SentMessages.Last().Text);
        Assert.Equal(before, bot.Sender.SentMenus.Count);
    }

    [Fact]
    public async Task LongCatalogueIsPagedAndFailedNextPageDoesNotSkipItems()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var names = Enumerable.Range(0, 22).Select(i => $"Z{i:D2}-" + new string('x', 196)).ToArray();
        await using (var db = fixture.CreateContext())
        {
            foreach (var name in names)
            {
                var service = new Service(name);
                db.AddRange(service, new StaffService(setup.Staff.Id, service.Id, 1.25m, 15));
            }
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var user = setup.Employee.TelegramUserId;
        await bot.Send("/offerings", user);
        await bot.Click(setup.Staff.DisplayName, user);
        var firstText = bot.Sender.SentMenus.Last().Text;
        Assert.Contains("Page 1 of 3", firstText);
        var staleRefresh = bot.Button("Refresh");
        var next = bot.Callback(bot.Button("Next offerings"), user);
        bot.Sender.FailNextSend(new IOException("Page reply failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(next));
        await bot.Deliver(next);
        var secondText = bot.Sender.SentMenus.Last().Text;
        Assert.Contains("Page 2 of 3", secondText);
        await bot.Deliver(bot.Callback(staleRefresh, user));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        Assert.Equal(secondText, bot.Sender.SentMenus.Last().Text);
        await bot.Click("Next offerings", user);
        var thirdText = bot.Sender.SentMenus.Last().Text;
        Assert.Contains("Page 3 of 3", thirdText);
        Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "Next offerings");
        foreach (var name in names)
            Assert.Single(new[] { firstText, secondText, thirdText }, text => text.Contains(name));
        Assert.All(new[] { firstText, secondText, thirdText }, text => Assert.InRange(text.Length, 1, 4096));
        await bot.Click("Previous offerings", user);
        Assert.Equal(secondText, bot.Sender.SentMenus.Last().Text);
        await bot.Click("Previous offerings", user);
        Assert.Equal(firstText, bot.Sender.SentMenus.Last().Text);
        Assert.DoesNotContain(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "Previous offerings");
        await bot.Click("Back", user);
        Assert.Contains("Choose an employee", bot.Sender.SentMenus.Last().Text);
    }

    [Fact]
    public async Task EmployeePageRetryPreservesPageAndBackReturnsToThatPage()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using (var db = fixture.CreateContext())
        {
            for (var i = 0; i < 21; i++)
            {
                var user = new User(Random.Shared.NextInt64(10_000_000_000, 80_000_000_000),
                    "Paged employee", null, DateTime.UtcNow);
                db.AddRange(user, new StaffMember(user.Id, $"! Overview {i:D2}-" + Guid.NewGuid()));
            }
            await db.SaveChangesAsync();
        }
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        var owner = setup.Owner.TelegramUserId;
        await bot.Send("/offerings", owner);
        var firstButtons = bot.Sender.SentMenus.Last().Buttons.Select(b => b.Text).ToArray();
        var next = bot.Callback(bot.Button("Next employees"), owner);
        bot.Sender.FailNextSend(new IOException("Employee page failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(next));
        await bot.Deliver(next);
        Assert.Contains("page 2 of", bot.Sender.SentMenus.Last().Text);
        var secondButtons = bot.Sender.SentMenus.Last().Buttons.Select(b => b.Text).ToArray();
        var selected = secondButtons.First(b => b.StartsWith("! Overview"));
        await bot.Click(selected, owner);
        Assert.Contains(selected, bot.Sender.SentMenus.Last().Text);
        await bot.Click("Back", owner);
        Assert.Equal(secondButtons, bot.Sender.SentMenus.Last().Buttons.Select(b => b.Text));
        await bot.Click("Previous employees", owner);
        Assert.Equal(firstButtons, bot.Sender.SentMenus.Last().Buttons.Select(b => b.Text));
    }

    [Fact]
    public async Task RestartedConversationRejectsOldButtonsAndCanReopenOverview()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        string old;
        await using (var bot = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId))
        {
            await bot.Send("/offerings", setup.Employee.TelegramUserId);
            old = bot.Button(setup.Staff.DisplayName);
        }
        await using var restarted = new TelegramManagementBot(fixture.ConnectionString, setup.Owner.TelegramUserId);
        await restarted.Deliver(restarted.Callback(old, setup.Employee.TelegramUserId));
        Assert.Contains("expired", restarted.Sender.CallbackAnswers.Last().Text);
        Assert.Empty(restarted.Sender.SentMenus);
        await restarted.Send("/offerings", setup.Employee.TelegramUserId);
        await restarted.Click(setup.Staff.DisplayName, setup.Employee.TelegramUserId);
        Assert.Contains(setup.ActiveService.Name, restarted.Sender.SentMenus.Last().Text);
    }

    private static async Task FindEmployee(TelegramManagementBot bot, string label, long user)
    {
        for (var page = 0; page < 100; page++)
        {
            if (bot.Sender.SentMenus.Last().Buttons.Any(b => b.Text == label)) return;
            await bot.Click("Next employees", user);
        }
        Assert.Fail("Seeded employee was not found in the paginated management list.");
    }
}
