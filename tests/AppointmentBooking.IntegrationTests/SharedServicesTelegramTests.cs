using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SharedServicesTelegramTests(PostgresFixture fixture)
{
    private const long Owner = 9_061_000_001;

    [Fact]
    public async Task OwnerCanCorrectInputAndCreateOneServiceEvenWhenReplyFails()
    {
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, Owner);
        await bot.Send("/start", Owner);
        await bot.Click("Shared services", Owner);
        await bot.Click("Create service", Owner);
        await bot.Send(new string('x', 201), Owner);
        Assert.Contains("must contain", bot.Sender.SentMenus.Last().Text);

        var name = "Telegram-" + Guid.NewGuid();
        var update = bot.Message("  " + name + "  ", Owner);
        bot.Sender.FailNextSend(new IOException("Reply failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(update));
        var repliesBeforeRetry = bot.Sender.SentMenus.Count;
        await bot.Deliver(update);
        Assert.Equal(repliesBeforeRetry + 1, bot.Sender.SentMenus.Count);
        Assert.Contains($"Service created: {name}.", bot.Sender.SentMenus.Last().Text);
        await bot.Send("ordinary text", Owner);
        await bot.Click("Back", Owner);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "Create service");

        await using var db = fixture.CreateContext();
        Assert.True((await db.Services.SingleAsync(s => s.Name == name)).IsActive);
        Assert.False(await db.Services.AnyAsync(s => s.Name == "ordinary text"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedReplyFailuresRecoverNavigationWithoutReplayingWrites(bool rename)
    {
        await using var db = fixture.CreateContext();
        var original = new Service("Retry-" + Guid.NewGuid());
        db.Services.Add(original);
        await db.SaveChangesAsync();
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, Owner);
        await bot.Send("/services", Owner);
        if (rename)
        {
            while (!bot.Sender.SentMenus.Last().Buttons.Any(b => b.Text == original.Name + " (active)"))
                await bot.Click("Next page", Owner);
            await bot.Click(original.Name + " (active)", Owner);
        }
        else
            await bot.Click("Create service", Owner);

        var name = "Saved-" + Guid.NewGuid();
        var update = bot.Message(name, Owner);
        var sentBefore = bot.Sender.SentMenus.Count;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            bot.Sender.FailNextSend(new IOException("Reply failed"));
            await Assert.ThrowsAsync<IOException>(() => bot.Deliver(update));
            Assert.Equal(sentBefore, bot.Sender.SentMenus.Count);
            var saved = await db.Services.AsNoTracking().SingleAsync(s => s.Name == name);
            if (rename) Assert.Equal(original.Id, saved.Id);
        }
        await bot.Deliver(update);
        Assert.Equal(sentBefore + 1, bot.Sender.SentMenus.Count);
        Assert.Contains(rename ? $"Service renamed to {name}." : $"Service created: {name}.",
            bot.Sender.SentMenus.Last().Text);
        var back = bot.Button("Back");
        await bot.Send("ordinary message", Owner);
        Assert.Equal(sentBefore + 1, bot.Sender.SentMenus.Count);
        Assert.Equal(back, bot.Button("Back"));
        await bot.Deliver(bot.Callback(back, Owner));
        Assert.Contains("Shared services", bot.Sender.SentMenus.Last().Text);
        Assert.DoesNotContain("Service created:", bot.Sender.SentMenus.Last().Text);
        Assert.DoesNotContain("Service renamed to", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Create service", Owner);
        Assert.Contains("Type a service name", bot.Sender.SentMenus.Last().Text);
        await bot.Click("Cancel", Owner);
        Assert.Equal(1, await db.Services.CountAsync(s => s.Name == name));
        Assert.False(await db.Services.AnyAsync(s => s.Name == "ordinary message"));
    }

    [Fact]
    public async Task CancelAndNonOwnerCommandsDoNotCreateServices()
    {
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, Owner);
        await bot.Send("/services", Owner);
        var stale = bot.Button("Create service");
        await bot.Click("Create service", Owner);
        await bot.Click("Cancel", Owner);
        var name = "Cancelled-" + Guid.NewGuid();
        await bot.Deliver(bot.Callback(stale, Owner));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Send(name, Owner);

        const long customer = Owner + 1;
        foreach (var command in new[] { "/services", "/services.create" })
        {
            await bot.Send(command, customer);
            Assert.Contains("permission", bot.Sender.SentMessages.Last().Text);
        }
        await bot.Send(name, customer);

        await using var db = fixture.CreateContext();
        Assert.False(await db.Services.AnyAsync(s => s.Name == name));
    }

    [Fact]
    public async Task OwnerRenamesInactiveServiceFromLaterPageWithoutChangingStatusOrReplayingWrite()
    {
        await using var db = fixture.CreateContext();
        var service = new Service("zz-Target-" + Guid.NewGuid());
        service.Deactivate();
        db.Services.Add(service);
        db.Services.AddRange(Enumerable.Range(0, 11).Select(i => new Service($"aa-Page-{i}")));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using var bot = new TelegramManagementBot(fixture.ConnectionString, Owner);
        await bot.Send("/services", Owner);
        var oldCreate = bot.Button("Create service");
        while (!bot.Sender.SentMenus.Last().Buttons.Any(b => b.Text == service.Name + " (inactive)"))
            await bot.Click("Next page", Owner);
        var listing = bot.Sender.SentMenus.Last().Text;
        await bot.Deliver(bot.Callback(oldCreate, Owner));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);

        await bot.Click(service.Name + " (inactive)", Owner);
        await bot.Send(new string('x', 201), Owner);
        Assert.Contains("must contain", bot.Sender.SentMenus.Last().Text);
        Assert.Equal(service.Name, (await db.Services.AsNoTracking().SingleAsync(s => s.Id == service.Id)).Name);

        var update = bot.Message("  zz-Renamed  ", Owner);
        bot.Sender.FailNextSend(new IOException("Reply failed"));
        await Assert.ThrowsAsync<IOException>(() => bot.Deliver(update));
        var renamed = await db.Services.SingleAsync(s => s.Id == service.Id);
        Assert.Equal("zz-Renamed", renamed.Name);
        Assert.False(renamed.IsActive);

        renamed.Rename("zz-Later");
        await db.SaveChangesAsync();
        var repliesBeforeRetry = bot.Sender.SentMenus.Count;
        await bot.Deliver(update);
        Assert.Equal(repliesBeforeRetry + 1, bot.Sender.SentMenus.Count);
        Assert.Contains("Service renamed to zz-Renamed.", bot.Sender.SentMenus.Last().Text);
        Assert.Equal("zz-Later", (await db.Services.AsNoTracking().SingleAsync(s => s.Id == service.Id)).Name);

        await bot.Click("Back", Owner);
        Assert.Equal(listing, bot.Sender.SentMenus.Last().Text);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "zz-Later (inactive)");
        var sentBefore = bot.Sender.SentMessages.Count;
        await bot.Send("/catalog", Owner);
        Assert.DoesNotContain(bot.Sender.SentMessages.Skip(sentBefore), message => message.Text.Contains("zz-Later"));
    }

    [Fact]
    public async Task CustomerCannotUseOwnersRenameButtonOrEnterRenameByCommand()
    {
        await using var db = fixture.CreateContext();
        var service = new Service("Protected-" + Guid.NewGuid());
        db.Services.Add(service);
        await db.SaveChangesAsync();

        await using var bot = new TelegramManagementBot(fixture.ConnectionString, Owner);
        await bot.Send("/services", Owner);
        while (!bot.Sender.SentMenus.Last().Buttons.Any(b => b.Text == service.Name + " (active)"))
            await bot.Click("Next page", Owner);
        const long customer = Owner + 1;
        await bot.Deliver(bot.Callback(bot.Button(service.Name + " (active)"), customer));
        Assert.Contains("expired", bot.Sender.CallbackAnswers.Last().Text);
        await bot.Send("/services.rename", customer);
        Assert.Contains("permission", bot.Sender.SentMessages.Last().Text);
        await bot.Send("Unauthorized rename", customer);

        Assert.Equal(service.Name, (await db.Services.AsNoTracking().SingleAsync(s => s.Id == service.Id)).Name);
    }
}
