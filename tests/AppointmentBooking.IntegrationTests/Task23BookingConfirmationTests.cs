using System.Net;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Api.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class Task23BookingConfirmationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ClickingTheRealConfirmationButtonTwiceKeepsOneSavedBooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: BookingTestSetup.ConfigureClock);
        var userId = seed.Client.TelegramUserId;
        var confirmData = await OpenBookingReviewAsync(seed, bot);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));

        await bot.Deliver(bot.Callback(confirmData, userId));
        Guid savedId;
        Guid requestId;
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            savedId = saved.Id;
            requestId = saved.RequestId;
            Assert.NotEqual(Guid.Empty, requestId);
            Assert.Equal(seed.Request.StaffServiceId, saved.StaffServiceId);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
        }

        // Another genuine click has a new update/callback ID, but the same old button data.
        await bot.Deliver(bot.Callback(confirmData, userId));

        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            Assert.Equal(savedId, saved.Id);
            Assert.Equal(requestId, saved.RequestId);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
        }
        Assert.Single(bot.Sender.SentMessages.Where(message => message.Text.StartsWith("Booking confirmed:")));
        Assert.Collection(bot.Sender.CallbackAnswers.TakeLast(2),
            answer => Assert.Null(answer.Text),
            answer => Assert.Contains("expired", answer.Text));
    }

    [Fact]
    public async Task FailedRealConfirmationReplyRetriesTheCommittedBookingWithTheSameRequestId()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: BookingTestSetup.ConfigureClock);
        var userId = seed.Client.TelegramUserId;
        var confirmData = await OpenBookingReviewAsync(seed, bot);
        var confirmation = bot.Callback(confirmData, userId);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));

        bot.Sender.FailNextSend(new HttpRequestException("Simulated failure sending the committed booking result"));
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Deliver(confirmation));

        // A fresh observer connection proves commit happened despite the failed reply.
        Guid savedId;
        Guid requestId;
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            savedId = saved.Id;
            requestId = saved.RequestId;
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
            Assert.NotEqual(Guid.Empty, requestId);
        }
        Assert.DoesNotContain(bot.Sender.SentMessages, message => message.Text.StartsWith("Booking confirmed:"));
        var store = bot.Services.GetRequiredService<ConversationStateStore>();
        using (var lease = await store.AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("book.confirm", lease.State.Step);
            Assert.Equal(requestId, lease.State.ConfirmationRequestId);
        }

        // Deliver the exact same update, including callback ID, in another operation scope.
        await bot.Deliver(confirmation);

        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            Assert.Equal(savedId, saved.Id);
            Assert.Equal(requestId, saved.RequestId);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
            Assert.Equal(seed.Request.StaffServiceId, saved.StaffServiceId);
        }
        Assert.Single(bot.Sender.SentMessages.Where(message => message.Text.StartsWith("Booking confirmed:")));
        var acknowledgements = bot.Sender.CallbackAnswers.Where(answer => answer.Id == confirmation.CallbackQuery!.Id).ToArray();
        Assert.Equal(2, acknowledgements.Length);
        Assert.All(acknowledgements, answer => Assert.Null(answer.Text));
        using (var lease = await store.AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("start", lease.State.Step);
            Assert.Null(lease.State.ConfirmationRequestId);
        }
    }

    [Fact]
    public async Task PollingRetriesFailedBookingReplyBeforeProcessingTheNextUpdate()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offsets = new List<int?>();
        var polls = 0;
        var webhookRemovals = 0;
        string? confirmData = null;
        Guid? savedId = null;
        Guid? requestId = null;
        TelegramManagementBot? bot = null;
        using var http = new HttpClient(new PollingHttpStub(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/deleteWebhook", StringComparison.Ordinal))
            {
                webhookRemovals++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.False(body.RootElement.TryGetProperty("drop_pending_updates", out var drop) && drop.GetBoolean());
                return TelegramResponse(true);
            }
            Assert.EndsWith("/getUpdates", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var offset = json.RootElement.TryGetProperty("offset", out var value) ? value.GetInt32() : (int?)null;
            offsets.Add(offset);
            polls++;
            if (polls == 2)
            {
                // This is the poller's automatic retry, after its first dispatch failed.
                Assert.Null(offset);
                await using var observer = fixture.CreateContext();
                var saved = await observer.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id, ct);
                savedId = saved.Id;
                requestId = saved.RequestId;
                Assert.Equal(BookingStatus.Confirmed, saved.Status);
                Assert.DoesNotContain(bot!.Sender.SentMessages, message => message.Text.StartsWith("Booking confirmed:"));
                Assert.DoesNotContain(bot.Sender.SentMessages, message => message.Text.StartsWith("Welcome,"));
                using var lease = await bot.Services.GetRequiredService<ConversationStateStore>()
                    .AcquireAsync(new(seed.Client.TelegramUserId, seed.Client.TelegramUserId), ct);
                Assert.Equal(requestId, lease.State.ConfirmationRequestId);
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
            return TelegramResponse(new object[]
            {
                new { update_id = 500, callback_query = new { id = "task23-polling-confirm", data = confirmData,
                    chat_instance = "test", @from = new { id = seed.Client.TelegramUserId, is_bot = false, first_name = "Client" },
                    message = new { message_id = 500, date = 0, chat = new { id = seed.Client.TelegramUserId, type = "private" } } } },
                new { update_id = 501, message = new { message_id = 501, date = 0, text = "/start",
                    @from = new { id = seed.Client.TelegramUserId, is_bot = false, first_name = "Client" },
                    chat = new { id = seed.Client.TelegramUserId, type = "private" } } }
            });
        }, finished));
        const string fakeToken = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345";
        await using var ownedBot = bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: services =>
            {
                BookingTestSetup.ConfigureClock(services);
                services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(
                    new TelegramBotClientOptions(fakeToken) { RetryCount = 0 }, http));
            });
        confirmData = await OpenBookingReviewAsync(seed, bot);
        bot.Sender.FailNextSend(new HttpRequestException("Simulated committed booking reply failure"));
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
        Assert.Equal(new int?[] { null, null, 502 }, offsets);
        Assert.Equal(1, webhookRemovals);
        Assert.NotNull(savedId);
        Assert.NotNull(requestId);
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            Assert.Equal(savedId, saved.Id);
            Assert.Equal(requestId, saved.RequestId);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
        }
        var messages = bot.Sender.SentMessages.Select(message => message.Text).ToList();
        Assert.Single(messages.Where(text => text.StartsWith("Booking confirmed:")));
        Assert.Single(messages.Where(text => text.StartsWith("Welcome,")));
        Assert.True(messages.FindIndex(text => text.StartsWith("Booking confirmed:")) <
                    messages.FindIndex(text => text.StartsWith("Welcome,")));
        Assert.Collection(bot.Sender.CallbackAnswers.Where(answer => answer.Id == "task23-polling-confirm"),
            answer => Assert.Null(answer.Text), answer => Assert.Null(answer.Text));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expiry")]
    public async Task OldConfirmationAfterCancelOrExpiryCannotCreateABooking(string situation)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        // BookingTestSetup creates its requested appointment two hours after its fixed clock.
        var clock = new ConversationTestClock { UtcNow = seed.Request.StartAtUtc.AddHours(-2) };
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: services => services.AddSingleton<AppointmentBooking.Application.Common.IClock>(clock));
        var userId = seed.Client.TelegramUserId;
        var confirmData = await OpenBookingReviewAsync(seed, bot);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));

        if (situation == "cancel")
        {
            await bot.Click("Cancel", userId);
            Assert.StartsWith("Welcome,", bot.Sender.SentMessages.Last().Text);
        }
        else
        {
            // Expiry is checked when the next update acquires conversation state.
            clock.UtcNow = clock.UtcNow.AddMinutes(30);
        }
        var messagesBeforeOldClick = bot.Sender.SentMessages.Count;
        var oldClick = bot.Callback(confirmData, userId);
        await bot.Deliver(oldClick);

        var rejection = Assert.Single(bot.Sender.CallbackAnswers.Where(answer => answer.Id == oldClick.CallbackQuery!.Id));
        Assert.Contains("expired", rejection.Text);
        Assert.Equal(messagesBeforeOldClick, bot.Sender.SentMessages.Count);
        Assert.DoesNotContain(bot.Sender.SentMessages, message => message.Text.StartsWith("Booking confirmed:"));
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));
        using (var lease = await bot.Services.GetRequiredService<ConversationStateStore>()
                   .AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("start", lease.State.Step);
            Assert.Null(lease.State.Booking);
            Assert.Null(lease.State.ConfirmationRequestId);
            Assert.Empty(lease.State.Selections);
            Assert.False(lease.State.CanGoBack);
        }

        // The rejection leaves recovery usable instead of leaving the user stuck.
        await bot.Send("/back", userId);
        Assert.StartsWith("Welcome,", bot.Sender.SentMessages.Last().Text);
    }

    [Fact]
    public async Task OldConfirmationAfterRestartCannotCreateABooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var userId = seed.Client.TelegramUserId;
        string confirmData;
        await using (var originalBot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
                         configureServices: BookingTestSetup.ConfigureClock))
        {
            confirmData = await OpenBookingReviewAsync(seed, originalBot);
        }

        // A fresh app host/provider has new in-memory state but uses the same persisted data.
        await using var restartedBot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: BookingTestSetup.ConfigureClock);
        var oldClick = restartedBot.Callback(confirmData, userId);
        await restartedBot.Deliver(oldClick);

        var rejection = Assert.Single(restartedBot.Sender.CallbackAnswers);
        Assert.Equal(oldClick.CallbackQuery!.Id, rejection.Id);
        Assert.Contains("expired", rejection.Text);
        Assert.Empty(restartedBot.Sender.SentMessages);
        using (var lease = await restartedBot.Services.GetRequiredService<ConversationStateStore>()
                   .AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("start", lease.State.Step);
            Assert.Null(lease.State.Booking);
            Assert.Null(lease.State.ConfirmationRequestId);
            Assert.False(lease.State.CanGoBack);
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.True(await db.Users.AnyAsync(user => user.Id == seed.Client.Id));
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));
        }

        await restartedBot.Send("/back", userId);
        Assert.StartsWith("Welcome,", restartedBot.Sender.SentMessages.Last().Text);
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await db.Users.CountAsync(user => user.TelegramUserId == userId));
    }

    [Fact]
    public async Task AnotherSenderInTheSameChatCannotUseTheClientsConfirmation()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: BookingTestSetup.ConfigureClock);
        var userId = seed.Client.TelegramUserId;
        var confirmData = await OpenBookingReviewAsync(seed, bot);
        var store = bot.Services.GetRequiredService<ConversationStateStore>();
        Guid originalRevision;
        BookingSelection? originalSelection;
        using (var lease = await store.AcquireAsync(new(userId, userId)))
        {
            originalRevision = lease.State.Revision;
            originalSelection = lease.State.Booking;
        }
        Assert.NotNull(originalSelection);
        long otherSenderId;
        await using (var db = fixture.CreateContext())
        {
            // Use the employee already seeded as another registered Telegram person.
            otherSenderId = await (from offering in db.StaffServices
                join staff in db.StaffMembers on offering.StaffMemberId equals staff.Id
                join user in db.Users on staff.UserId equals user.Id
                where offering.Id == seed.Request.StaffServiceId
                select user.TelegramUserId).SingleAsync();
        }
        Assert.NotEqual(userId, otherSenderId);
        var copiedClick = bot.Callback(confirmData, otherSenderId);
        // Keep the original destination chat: the sender must still be checked independently.
        copiedClick.CallbackQuery!.Message!.Chat.Id = userId;
        var messagesBeforeCopiedClick = bot.Sender.SentMessages.Count;
        await bot.Deliver(copiedClick);

        var rejection = Assert.Single(bot.Sender.CallbackAnswers.Where(answer => answer.Id == copiedClick.CallbackQuery.Id));
        Assert.Contains("expired", rejection.Text);
        Assert.Equal(messagesBeforeCopiedClick, bot.Sender.SentMessages.Count);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.StaffServiceId == seed.Request.StaffServiceId));
        using (var lease = await store.AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("book.confirm", lease.State.Step);
            Assert.Equal(originalRevision, lease.State.Revision);
            Assert.Equal(originalSelection, lease.State.Booking);
        }

        // Rejecting the copied click must not consume or reset the actual client's review.
        await bot.Deliver(bot.Callback(confirmData, userId));
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.StaffServiceId == seed.Request.StaffServiceId);
            Assert.Equal(seed.Client.Id, saved.ClientUserId);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
        }
        Assert.Single(bot.Sender.SentMessages.Where(message => message.Text.StartsWith("Booking confirmed:")));
    }

    [Fact]
    public async Task BackRejectsOldConfirmationAndAllowsANewSelectedTime()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var bot = new TelegramManagementBot(fixture.ConnectionString, ownerId: 0,
            configureServices: BookingTestSetup.ConfigureClock);
        var userId = seed.Client.TelegramUserId;
        var store = bot.Services.GetRequiredService<ConversationStateStore>();
        var oldConfirmData = await OpenBookingReviewAsync(seed, bot);
        DateTimeOffset? originalStart;
        using (var lease = await store.AcquireAsync(new(userId, userId)))
            originalStart = lease.State.Booking!.SlotStartUtc;
        Assert.NotNull(originalStart);

        await bot.Click("Back", userId);
        Assert.Equal("Choose a time:", bot.Sender.SentMenus.Last().Text);
        using (var lease = await store.AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("book.time", lease.State.Step);
            Assert.Equal(seed.Request.StaffServiceId, lease.State.Booking!.StaffServiceId);
        }
        var timeButton = bot.Sender.SentMenus.Last().Buttons.Last(button =>
            CallbackDataParser.TryParse(button.CallbackData, out var callback) && callback!.Route == "book.time");
        var beforeRejectedClick = bot.Sender.SentMessages.Count;
        var staleClick = bot.Callback(oldConfirmData, userId);
        await bot.Deliver(staleClick);
        Assert.Contains("expired", Assert.Single(bot.Sender.CallbackAnswers
            .Where(answer => answer.Id == staleClick.CallbackQuery!.Id)).Text);
        Assert.Equal(beforeRejectedClick, bot.Sender.SentMessages.Count);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));

        // Choose another time and build a fresh review. The earlier review is still invalid.
        await bot.Deliver(bot.Callback(timeButton.CallbackData, userId));
        Assert.StartsWith("Review booking:", bot.Sender.SentMessages.Last().Text);
        var newConfirmData = bot.Button("Confirm booking");
        Assert.NotEqual(oldConfirmData, newConfirmData);
        DateTimeOffset? selectedStart;
        using (var lease = await store.AcquireAsync(new(userId, userId)))
        {
            Assert.Equal("book.confirm", lease.State.Step);
            selectedStart = lease.State.Booking!.SlotStartUtc;
        }
        Assert.NotNull(selectedStart);
        Assert.NotEqual(originalStart, selectedStart);
        var oldClickOnNewReview = bot.Callback(oldConfirmData, userId);
        await bot.Deliver(oldClickOnNewReview);
        Assert.Contains("expired", Assert.Single(bot.Sender.CallbackAnswers
            .Where(answer => answer.Id == oldClickOnNewReview.CallbackQuery!.Id)).Text);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.Bookings.AnyAsync(booking => booking.ClientUserId == seed.Client.Id));

        await bot.Deliver(bot.Callback(newConfirmData, userId));
        await using (var db = fixture.CreateContext())
        {
            var saved = await db.Bookings.SingleAsync(booking => booking.ClientUserId == seed.Client.Id);
            Assert.Equal(selectedStart.Value.UtcDateTime, saved.StartsAtUtc);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
        }
        Assert.Single(bot.Sender.SentMessages.Where(message => message.Text.StartsWith("Booking confirmed:")));
    }

    private sealed class ConversationTestClock : AppointmentBooking.Application.Common.IClock
    {
        public DateTime UtcNow { get; set; }
    }

    private static HttpResponseMessage TelegramResponse(object result) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }), Encoding.UTF8, "application/json") };

    private sealed class PollingHttpStub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TaskCompletionSource finished) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try { return await respond(request, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Polling retries exceptions; surface failed test assertions immediately.
                finished.TrySetException(error);
                throw;
            }
        }
    }

    private async Task<string> OpenBookingReviewAsync(BookingTestSetup seed, TelegramManagementBot bot)
    {
        var userId = seed.Client.TelegramUserId;
        var store = bot.Services.GetRequiredService<ConversationStateStore>();
        Guid staffId;
        await using (var db = fixture.CreateContext())
            staffId = (await db.StaffServices.SingleAsync(offering => offering.Id == seed.Request.StaffServiceId)).StaffMemberId;

        async Task SelectItem(string route, Guid? expectedId = null)
        {
            string data;
            using (var lease = await store.AcquireAsync(new(userId, userId)))
            {
                data = bot.Sender.SentMenus.Last().Buttons.First(button =>
                    CallbackDataParser.TryParse(button.CallbackData, out var callback) && callback!.Route == route &&
                    lease.State.TryResolveItem(callback, out var selected) &&
                    (!expectedId.HasValue || selected == expectedId.Value)).CallbackData;
            }
            // Release the inspection lease before the dispatcher acquires its own.
            await bot.Deliver(bot.Callback(data, userId));
        }

        await bot.Send("/book", userId);
        await SelectItem("book.staff", staffId);
        await SelectItem("book.service", seed.Request.StaffServiceId);
        await bot.Send(seed.Request.StartAtUtc.ToString("yyyy-MM-dd"), userId);
        await SelectItem("book.time");
        Assert.StartsWith("Review booking:", bot.Sender.SentMessages.Last().Text);
        using (var lease = await store.AcquireAsync(new(userId, userId)))
            Assert.Equal("book.confirm", lease.State.Step);
        return bot.Button("Confirm booking");
    }
}
