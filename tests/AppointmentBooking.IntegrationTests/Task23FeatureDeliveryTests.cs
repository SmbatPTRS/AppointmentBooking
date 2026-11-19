using System.Net;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class Task23FeatureDeliveryTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingRetriesCommittedFeatureReplyWithoutOverwritingALaterChange(bool schedule)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        Guid staffId;
        long employeeId;
        await using (var db = fixture.CreateContext())
        {
            var employee = await (from offering in db.StaffServices
                join staff in db.StaffMembers on offering.StaffMemberId equals staff.Id
                join user in db.Users on staff.UserId equals user.Id
                where offering.Id == seed.Request.StaffServiceId
                select new { StaffId = staff.Id, TelegramId = user.TelegramUserId }).SingleAsync();
            staffId = employee.StaffId;
            employeeId = employee.TelegramId;
        }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offsets = new List<int?>();
        var polls = 0;
        var deletes = 0;
        var observedCommit = false;
        var messagesBeforePolling = 0;
        var dayOff = new DateOnly(2027, 3, 1);
        var input = schedule ? "2027-03-01" : "Delivered name";
        TelegramManagementBot? bot = null;
        using var http = new HttpClient(new HttpStub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/deleteWebhook", StringComparison.Ordinal))
            {
                deletes++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.False(body.RootElement.TryGetProperty("drop_pending_updates", out var drop) && drop.GetBoolean());
                return Response(true);
            }
            Assert.EndsWith("/getUpdates", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var offset = json.RootElement.TryGetProperty("offset", out var value) ? value.GetInt32() : (int?)null;
            offsets.Add(offset);
            polls++;
            if (polls == 2)
            {
                Assert.Null(offset);
                Assert.Equal(messagesBeforePolling, bot!.Sender.SentMessages.Count);
                await using var observer = fixture.CreateContext();
                if (schedule)
                {
                    var saved = await observer.StaffDaysOff.SingleAsync(row => row.StaffMemberId == staffId, ct);
                    Assert.Equal(dayOff, saved.Date);
                    // A later edit removes the day off. Retrying the old reply must not recreate it.
                    observer.StaffDaysOff.Remove(saved);
                }
                else
                {
                    var saved = await observer.StaffMembers.SingleAsync(row => row.Id == staffId, ct);
                    Assert.Equal("Delivered name", saved.DisplayName);
                    // A later owner edit must survive redelivery of the older rename.
                    saved.Rename("Later owner edit");
                }
                await observer.SaveChangesAsync(ct);
                observedCommit = true;
            }
            if (polls == 3)
            {
                Assert.Equal(502, offset);
                finished.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Expected receiver shutdown.");
            }
            Assert.InRange(polls, 1, 2);
            Assert.Null(offset);
            object Message(int id, string text) => new { update_id = id,
                message = new { message_id = id, date = 0, text,
                    @from = new { id = employeeId, is_bot = false, first_name = "Employee" },
                    chat = new { id = employeeId, type = "private" } } };
            return Response(new[] { Message(500, input), Message(501, "/start") });
        }, finished));
        const string token = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345";
        await using var ownedBot = bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: services =>
            {
                BookingTestSetup.ConfigureClock(services);
                services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(
                    new TelegramBotClientOptions(token) { RetryCount = 0 }, http));
            });
        await bot.Send(schedule ? "/schedule" : "/profile", employeeId);
        if (schedule) await bot.Click("Add day off", employeeId);
        messagesBeforePolling = bot.Sender.SentMessages.Count;
        bot.Sender.FailNextSend(new HttpRequestException("Simulated committed feature reply failure"));
        using var polling = new TelegramPollingService(bot.Services,
            bot.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TelegramOptions { Enabled = true, Mode = "Polling",
                PollingRetryDelaySeconds = 1, PollingMaxRetryDelaySeconds = 1 }),
            NullLogger<TelegramPollingService>.Instance);
        try
        {
            await polling.StartAsync(default);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await polling.StopAsync(shutdown.Token);
            if (polling.ExecuteTask is { } execution) await execution.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(observedCommit);
        Assert.Equal(new int?[] { null, null, 502 }, offsets);
        Assert.Equal(1, deletes);
        await using (var db = fixture.CreateContext())
        {
            if (schedule) Assert.False(await db.StaffDaysOff.AnyAsync(row => row.StaffMemberId == staffId));
            else Assert.Equal("Later owner edit", (await db.StaffMembers.SingleAsync(row => row.Id == staffId)).DisplayName);
        }
        var replies = bot.Sender.SentMessages.Skip(messagesBeforePolling).Select(message => message.Text).ToArray();
        Assert.Equal(2, replies.Length);
        if (schedule)
        {
            Assert.StartsWith("Hours:", replies[0]);
            Assert.Contains("Days off: 0", replies[0]);
        }
        else Assert.Equal("Your display name was updated to Delivered name.", replies[0]);
        Assert.StartsWith("Welcome,", replies[1]);
    }

    private static HttpResponseMessage Response(object result) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }), Encoding.UTF8, "application/json") };

    private sealed class HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TaskCompletionSource finished) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try { return await respond(request, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { finished.TrySetException(error); throw; }
        }
    }
}
