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
        await bot.Deliver(update);
        Assert.Contains(name, bot.Sender.SentMenus.Last().Text);
        await bot.Send("ordinary text", Owner);
        await bot.Click("Back", Owner);
        Assert.Contains(bot.Sender.SentMenus.Last().Buttons, b => b.Text == "Create service");

        await using var db = fixture.CreateContext();
        Assert.True((await db.Services.SingleAsync(s => s.Name == name)).IsActive);
        Assert.False(await db.Services.AnyAsync(s => s.Name == "ordinary text"));
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
}
