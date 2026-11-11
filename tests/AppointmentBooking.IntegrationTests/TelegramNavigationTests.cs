using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AppointmentBooking.IntegrationTests;

public sealed class TelegramNavigationTests
{
    [Fact]
    public async Task ItemSelectionTextBackAndCancelFollowTheSharedContract()
    {
        var test = new Harness();
        await test.Message("/demo.staff");
        var listButton = test.Button("Ada");
        await test.Click(listButton);
        Assert.Contains(test.StaffId.ToString(), test.Sender.SentMenus.Last().Text);
        var oldRename = test.Button("Rename");
        await test.Click(oldRename);
        await test.Message("New name");
        Assert.Equal("New name", (await test.Draft())!.PendingName);
        await test.Click(test.Button("Back"));
        Assert.Contains("Employee details", test.Sender.SentMenus.Last().Text);
        Assert.Equal(new ManagementNavigationExample.ManagementDraft(test.StaffId), await test.Draft());
        await test.Click(oldRename);
        Assert.Contains("expired", test.Sender.CallbackAnswers.Last().Text);
        Assert.Contains("Employee details", test.Sender.SentMenus.Last().Text);
        await test.Click(test.Button("Back"));
        Assert.Equal("Staff list", test.Sender.SentMenus.Last().Text);
        Assert.Null(await test.Draft());
        await test.Click(listButton);
        Assert.Contains("expired", test.Sender.CallbackAnswers.Last().Text);
        await test.Click(test.Button("Ada"));
        await test.Click(test.Button("Rename"));
        await test.Message("Abandoned name");
        var oldBack = test.Button("Back");
        await test.Click(test.Button("Cancel"));
        Assert.Equal("Main menu", test.Sender.SentMenus.Last().Text);
        Assert.Null(await test.Draft());
        using (var lease = await test.Store.AcquireAsync(new(42, -999)))
        {
            Assert.Empty(lease.State.Selections);
            Assert.Null(lease.State.ConfirmationRequestId);
            Assert.False(lease.State.CanGoBack);
        }
        await test.Click(oldBack);
        Assert.Contains("expired", test.Sender.CallbackAnswers.Last().Text);
    }

    [Theory]
    [InlineData("other-user")]
    [InlineData("other-chat")]
    [InlineData("expired")]
    [InlineData("restart")]
    [InlineData("reentry")]
    [InlineData("unknown-key")]
    [InlineData("wrong-route")]
    [InlineData("missing-key")]
    public async Task UnsafeItemButtonsCannotSelectAnEmployee(string situation)
    {
        var test = new Harness();
        await test.Message("/demo.staff");
        var button = test.Button("Ada");
        if (situation == "expired") test.Clock.UtcNow = test.Clock.UtcNow.AddMinutes(30);
        if (situation == "restart") test = new Harness();
        if (situation == "reentry") await test.Message("/demo.staff");
        Assert.True(CallbackDataParser.TryParse(button, out var parsed));
        if (situation == "unknown-key") button = CallbackDataParser.FormatItem(parsed!.Route, parsed.Revision, "unknown");
        if (situation == "wrong-route") button = CallbackDataParser.FormatItem("demo.rename", parsed!.Revision, parsed.ButtonKey!);
        if (situation == "missing-key") button = CallbackDataParser.Format(parsed!.Route, parsed.Revision);
        await test.Click(button, situation == "other-user" ? 43 : 42, situation == "other-chat" ? -998 : -999);
        Assert.Null(await test.Draft());
        Assert.NotNull(test.Sender.CallbackAnswers.Last().Text);
        Assert.DoesNotContain(test.Sender.SentMenus, menu => menu.Text.StartsWith("Employee details"));
    }

    [Theory]
    [InlineData("/back")]
    [InlineData("/cancel")]
    public async Task NavigationWithoutStateSafelyReturnsToMenu(string command)
    {
        var test = new Harness();
        await test.Message(command);
        Assert.Equal("Main menu", test.Sender.SentMenus.Last().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBackRenderRetriesTheSameScreenWithoutPoppingAgain(bool callback)
    {
        var test = new Harness();
        await test.Message("/demo.staff");
        await test.Click(test.Button("Ada"));
        await test.Click(test.Button("Rename"));
        var update = callback ? test.Callback(test.Button("Back")) : test.Text("/back");
        test.Sender.FailNextSend(new HttpRequestException("Temporary send failure"));
        await Assert.ThrowsAsync<HttpRequestException>(() => test.Dispatcher.DispatchAsync(update));
        await test.Dispatcher.DispatchAsync(update);
        Assert.StartsWith("Employee details", test.Sender.SentMenus.Last().Text);
        Assert.Equal(test.StaffId, (await test.Draft())!.StaffId);
        await test.Message("/back");
        Assert.Equal("Staff list", test.Sender.SentMenus.Last().Text);
    }

    [Fact]
    public async Task DeliveryRetryKeepsTheConfirmationIdentity()
    {
        var test = new Harness();
        Guid? requestId = null;
        test = new Harness(extra: new Handler("confirm", async (context, ct) =>
        {
            context.State.ConfirmationRequestId ??= Guid.NewGuid();
            if (requestId.HasValue) Assert.Equal(requestId, context.State.ConfirmationRequestId);
            requestId = context.State.ConfirmationRequestId;
            await test!.Sender.SendTextAsync(context.ChatId, "Saved", ct);
        }));
        var update = test.Text("/confirm");
        test.Sender.FailNextSend(new HttpRequestException("Temporary send failure"));
        await Assert.ThrowsAsync<HttpRequestException>(() => test.Dispatcher.DispatchAsync(update));
        await test.Dispatcher.DispatchAsync(update);
        Assert.NotNull(requestId);
        using var lease = await test.Store.AcquireAsync(new(42, -999));
        Assert.Equal(requestId, lease.State.ConfirmationRequestId);
    }

    [Fact]
    public async Task FailedItemScreenSendRetriesWithTheSameResolvedItemAndOneHistoryEntry()
    {
        var test = new Harness();
        await test.Message("/demo.staff");
        var update = test.Callback(test.Button("Ada"));
        test.Sender.FailNextSend(new HttpRequestException("Temporary send failure"));
        await Assert.ThrowsAsync<HttpRequestException>(() => test.Dispatcher.DispatchAsync(update));
        await test.Dispatcher.DispatchAsync(update);
        Assert.Contains(test.StaffId.ToString(), test.Sender.SentMenus.Last().Text);
        await test.Message("/back");
        Assert.Equal("Staff list", test.Sender.SentMenus.Last().Text);
        Assert.Null(await test.Draft());
    }

    [Fact]
    public async Task FailedFlowEntrySendCanBeRetriedWithoutLosingItsScreen()
    {
        var test = new Harness();
        var update = test.Text("/demo.staff");
        test.Sender.FailNextSend(new HttpRequestException("Temporary send failure"));
        await Assert.ThrowsAsync<HttpRequestException>(() => test.Dispatcher.DispatchAsync(update));
        await test.Dispatcher.DispatchAsync(update);
        Assert.Equal("Staff list", test.Sender.SentMenus.Last().Text);
        await test.Message("/back");
        Assert.Equal("Main menu", test.Sender.SentMenus.Last().Text);
    }

    [Fact]
    public async Task TwoFastClicksValidateUnderOneLeaseWhileAnotherUserCanProceed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = 0;
        var test = new Harness(new Handler("slow", async (context, ct) =>
        {
            actions++;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            context.State.BeginStep("slow", DateTime.UtcNow);
        }));
        await test.Message("/start");
        string data;
        using (var lease = await test.Store.AcquireAsync(new(42, -999)))
            data = lease.State.Button("Act", "slow").CallbackData;
        var first = test.Click(data);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = test.Click(data);
        try
        {
            Assert.False(second.IsCompleted);
            var otherUser = TelegramFoundationTests.Message("/start", senderId: 43);
            otherUser.Id = 100;
            await test.Dispatcher.DispatchAsync(otherUser).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, actions);
        Assert.Contains("expired", test.Sender.CallbackAnswers.Last().Text);
    }

    private sealed class Harness
    {
        public readonly Clock Clock = new();
        public readonly FakeTelegramClient Sender = new();
        public readonly ConversationStateStore Store;
        public readonly TelegramUpdateDispatcher Dispatcher;
        public readonly Guid StaffId = Guid.NewGuid();
        private int updateId;
        public Harness(ITelegramHandler? extra = null)
        {
            Store = new ConversationStateStore(Clock);
            var handlers = ManagementNavigationExample.Create(Sender, Clock, StaffId).ToList();
            handlers.Add(new Handler("start", async (context, ct) =>
            {
                context.State.Reset(Clock.UtcNow);
                await Sender.SendMenuAsync(context.ChatId, "Main menu", [context.State.Button("Staff", "demo.staff")], ct);
            }));
            if (extra is not null) handlers.Add(extra);
            Dispatcher = new(handlers, new TelegramActorResolver(new UserService(new TelegramTransportTests.UsersRepository(), Clock)),
                Sender, Store, Options.Create(new TelegramOptions()));
        }
        public string Button(string label) => Sender.SentMenus.Last().Buttons.Single(button => button.Text == label).CallbackData;
        public Update Text(string text)
        { var update = TelegramFoundationTests.Message(text); update.Id = ++updateId; return update; }
        public Update Callback(string data, long user = 42, long chat = -999) => new()
        {
            Id = ++updateId,
            CallbackQuery = new CallbackQuery { Id = updateId.ToString(), Data = data, ChatInstance = "test",
                From = new User { Id = user, FirstName = "Ada" },
                Message = new Message { Chat = new Chat { Id = chat, Type = ChatType.Private } } }
        };
        public Task Message(string text) => Dispatcher.DispatchAsync(Text(text));
        public Task Click(string data, long user = 42, long chat = -999) => Dispatcher.DispatchAsync(Callback(data, user, chat));
        public async Task<ManagementNavigationExample.ManagementDraft?> Draft()
        {
            using var lease = await Store.AcquireAsync(new(42, -999));
            return lease.State.GetFeatureData<ManagementNavigationExample.ManagementDraft>(ManagementNavigationExample.Feature);
        }
    }
    private sealed class Handler(string route, Func<TelegramHandlerContext, CancellationToken, Task> handle) : ITelegramHandler
    {
        public string Route => route;
        public Task HandleAsync(TelegramHandlerContext context, CancellationToken ct) => handle(context, ct);
    }
    private sealed class Clock : IClock
    { public DateTime UtcNow { get; set; } = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc); }
}
