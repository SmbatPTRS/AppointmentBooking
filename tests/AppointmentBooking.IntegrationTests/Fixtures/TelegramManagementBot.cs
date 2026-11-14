using AppointmentBooking.Api.Telegram;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AppointmentBooking.IntegrationTests.Fixtures;

public sealed class TelegramManagementBot : IAsyncDisposable
{
    private readonly ApiFactory factory;
    private readonly WebApplicationFactory<Program> host;
    private int updateId;
    public FakeTelegramClient Sender { get; } = new();
    public IServiceProvider Services => host.Services;

    public TelegramManagementBot(string connectionString, long ownerId)
    {
        factory = new ApiFactory(connectionString);
        host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Owner:TelegramUserId"] = ownerId.ToString() }));
            builder.ConfigureTestServices(services => services.AddSingleton<ITelegramMessageSender>(Sender));
        });
    }

    public Update Message(string text, long userId)
    {
        var update = TelegramFoundationTests.Message(text, userId, userId);
        update.Id = ++updateId;
        return update;
    }

    public Update Callback(string data, long userId) => new()
    {
        Id = ++updateId,
        CallbackQuery = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"), Data = data, ChatInstance = "test",
            From = new User { Id = userId, FirstName = "Caller" },
            Message = new Message { Chat = new Chat { Id = userId, Type = ChatType.Private } }
        }
    };

    public string Button(string label) => Sender.SentMenus.Last().Buttons.Single(button => button.Text == label).CallbackData;
    public Task Send(string text, long userId) => Deliver(Message(text, userId));
    public Task Click(string label, long userId) => Deliver(Callback(Button(label), userId));

    public async Task Deliver(Update update)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(update);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await factory.DisposeAsync();
    }
}
