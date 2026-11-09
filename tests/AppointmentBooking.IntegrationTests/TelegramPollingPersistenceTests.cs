using System.Net;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TelegramPollingPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StartSurvivesSendFailureAndReceiverRestartWithOnePersistedUser()
    {
        const long telegramId = 8_000_000_020;
        for (var restart = 0; restart < 2; restart++)
        {
            var sender = new FakeTelegramClient();
            if (restart == 0) sender.FailNextSend(new HttpRequestException("Simulated post-registration send failure"));
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deletes = 0;
            using var http = new HttpClient(new HttpStub(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/deleteWebhook", StringComparison.Ordinal))
                { deletes++; return Json("{\"ok\":true,\"result\":true}"); }
                if (sender.SentMenus.Count > 0)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal(101, body.RootElement.GetProperty("offset").GetInt32());
                    finished.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                return Json(JsonSerializer.Serialize(new
                {
                    ok = true, result = new[] { new { update_id = 100,
                        message = new { message_id = 100, date = 0, text = "/start",
                            @from = new { id = telegramId, is_bot = false, first_name = "Ada" },
                            chat = new { id = telegramId, type = "private" } } } }
                }));
            }));
            const string token = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345";
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString,
                ["Telegram:Enabled"] = "true", ["Telegram:Mode"] = "Polling", ["Telegram:BotToken"] = token
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddInfrastructure(config);
            services.AddScoped<IStaffRepository, StaffRepository>();
            services.AddTelegramInfrastructure();
            services.AddSingleton<ITelegramMessageSender>(sender);
            services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(new TelegramBotClientOptions(token) { RetryCount = 0 }, http));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var polling = Assert.Single(provider.GetServices<IHostedService>());
            try
            {
                await polling.StartAsync(default);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await polling.StopAsync(deadline.Token);
            }
            Assert.Equal(1, deletes);
            Assert.Single(sender.SentMenus);
            await using var db = fixture.CreateContext();
            Assert.Equal(1, await db.Users.CountAsync(user => user.TelegramUserId == telegramId));
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
}
