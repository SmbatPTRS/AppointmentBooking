using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TelegramUserRegistrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StartPersistsOneRealUserAcrossSeparateOperationScopes()
    {
        const long telegramId = 7_777_777_777;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString,
            ["Telegram:Enabled"] = "false", ["Owner:TelegramUserId"] = "0"
        }).Build();
        var sender = new FakeTelegramClient();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);
        services.AddScoped<IStaffRepository, StaffRepository>();
        services.AddTelegramInfrastructure();
        services.AddSingleton<ITelegramMessageSender>(sender);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        for (var visit = 0; visit < 2; visit++)
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>()
                .DispatchAsync(TelegramFoundationTests.Message("/start", telegramId, -12345));
        }
        await using var db = fixture.CreateContext();
        var users = await db.Users.Where(user => user.TelegramUserId == telegramId).ToListAsync();
        var stored = Assert.Single(users);
        Assert.Equal("Ada Lovelace", stored.DisplayName);
        Assert.Equal("ada", stored.TelegramUsername);
        Assert.Equal(2, sender.SentMenus.Count);
        Assert.All(sender.SentMenus, menu => Assert.Equal(-12345, menu.ChatId));
    }
}
