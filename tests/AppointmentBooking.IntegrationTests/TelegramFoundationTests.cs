using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.Handlers;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using AppUser = AppointmentBooking.Domain.Entities.User;
using TelegramUser = Telegram.Bot.Types.User;

namespace AppointmentBooking.IntegrationTests;

// Uses A's actual UserService with a test-only repository and no database/network.
public sealed class TelegramFoundationTests
{
    [Fact]
    public async Task StartRegistersSenderNotChatAndReturnsClientMenuOnRepeatedVisits()
    {
        var test = new Harness();
        await test.Dispatcher.DispatchAsync(Message("/start"));
        await test.Dispatcher.DispatchAsync(Message("/start"));
        Assert.Equal(42, Assert.Single(test.Users.Stored).TelegramUserId);
        Assert.Equal("Ada Lovelace", test.Users.Stored.Single().DisplayName);
        Assert.Equal("ada", test.Users.Stored.Single().TelegramUsername);
        Assert.Equal(2, test.Sender.SentMenus.Count);
        var menu = test.Sender.SentMenus.Last();
        Assert.Equal(-999, menu.ChatId);
        Assert.Equal(new[] { "book", "bookings" }, menu.Buttons.Select(Route));
    }

    [Theory]
    [InlineData(true, false, false, true, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, true, false, false, false)]
    public async Task MenuReflectsOwnerAndCurrentStaffActivation(bool owner, bool staff, bool active,
        bool expectSettings, bool expectSchedule)
    {
        var test = new Harness(owner);
        if (staff)
        {
            var user = await test.Users.InsertOrGetAsync(new AppUser(42, "Ada", null, test.Clock.UtcNow));
            test.Staff.Profile = new StaffMember(user.Id, "Ada");
            if (!active) test.Staff.Profile.Deactivate();
        }
        await test.Dispatcher.DispatchAsync(Message("/start"));
        var routes = test.Sender.SentMenus.Single().Buttons.Select(Route).ToArray();
        Assert.Equal(expectSettings, routes.Contains("settings"));
        Assert.Equal(expectSchedule, routes.Contains("schedule"));
        Assert.Equal(owner || staff, routes.Contains("appointments"));
    }

    [Fact]
    public async Task SampleHandlerReceivesTrustedInternalActorAndCurrentState()
    {
        TelegramHandlerContext? received = null;
        var test = new Harness(extra: new Handler("book", (context, _) => { received = context; return Task.CompletedTask; }));
        await test.Dispatcher.DispatchAsync(Message("/start"));
        await test.Dispatcher.DispatchAsync(Callback(test.BookButton));
        Assert.Equal(test.Users.Stored.Single().Id, received!.Caller.Actor.UserId);
        Assert.Equal(42, received.Caller.TelegramUserId);
        Assert.Equal(-999, received.ChatId);
        Assert.Equal("book", received.Callback!.Route);
        Assert.Null(Assert.Single(test.Sender.CallbackAnswers).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user:999:delete")]
    [InlineData("1:00000000000000000000000000000000:book")]
    [InlineData("1:11111111111111111111111111111111:../delete")]
    public async Task MalformedCallbackCannotCreateUserOrCallHandler(string? data)
    {
        var calls = 0;
        var test = new Harness(extra: new Handler("book", (_, _) => { calls++; return Task.CompletedTask; }));
        var result = await test.Dispatcher.DispatchAsync(Callback(data));
        Assert.Equal(TelegramDispatchResult.Handled, result);
        Assert.Empty(test.Users.Stored);
        Assert.Equal(0, calls);
        Assert.Contains("Invalid", Assert.Single(test.Sender.CallbackAnswers).Text);
    }

    [Theory]
    [InlineData("other-user")]
    [InlineData("other-chat")]
    [InlineData("old-screen")]
    [InlineData("expired")]
    public async Task OldOrCopiedButtonsCannotCallFeatureHandler(string situation)
    {
        var calls = 0;
        var test = new Harness(extra: new Handler("book", (_, _) => { calls++; return Task.CompletedTask; }));
        await test.Dispatcher.DispatchAsync(Message("/start"));
        var button = test.BookButton;
        if (situation == "old-screen") await test.Dispatcher.DispatchAsync(Message("/start"));
        if (situation == "expired") test.Clock.UtcNow = test.Clock.UtcNow.AddMinutes(30);
        await test.Dispatcher.DispatchAsync(Callback(button, situation == "other-user" ? 43 : 42,
            situation == "other-chat" ? -998 : -999));
        Assert.Equal(0, calls);
        Assert.NotNull(test.Sender.CallbackAnswers.Single().Text);
        Assert.Single(test.Users.Stored);
    }

    [Fact]
    public async Task SendFailureIsRetryableAndKeepsTheSameConfirmationIdentity()
    {
        var ids = new List<Guid>();
        Harness? test = null;
        test = new Harness(extra: new Handler("book", async (context, ct) =>
        {
            context.State.ConfirmationRequestId ??= Guid.NewGuid();
            ids.Add(context.State.ConfirmationRequestId.Value);
            await test!.Sender.SendTextAsync(context.ChatId, "Confirmed", ct);
        }));
        await test.Dispatcher.DispatchAsync(Message("/start"));
        var callback = Callback(test.BookButton);
        test.Sender.FailNextSend(new HttpRequestException("Simulated send failure"));
        await Assert.ThrowsAsync<HttpRequestException>(() => test.Dispatcher.DispatchAsync(callback));
        await test.Dispatcher.DispatchAsync(callback);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
        Assert.Single(test.Users.Stored);
    }

    [Fact]
    public async Task ExpectedRejectionIsHandledButUnexpectedProcessingFailurePropagates()
    {
        var test = new Harness(extra: new Handler("book", (_, _) => throw new AppException(
            ErrorCodes.Forbidden, "Hidden detail", AppErrorKind.Forbidden)));
        await test.Dispatcher.DispatchAsync(Message("/start"));
        Assert.Equal(TelegramDispatchResult.Handled, await test.Dispatcher.DispatchAsync(Callback(test.BookButton)));
        Assert.Contains("permission", test.Sender.SentMessages.Last().Text);
        var failed = new Harness(extra: new Handler("book", (_, _) => throw new InvalidOperationException("Broken handler")));
        await failed.Dispatcher.DispatchAsync(Message("/start"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Dispatcher.DispatchAsync(Callback(failed.BookButton)));
    }

    [Theory]
    [InlineData("/back")]
    [InlineData("/cancel")]
    public async Task BasicNavigationReturnsToMainMenuAndRejectsOldButtons(string command)
    {
        var test = new Harness();
        await test.Dispatcher.DispatchAsync(Message("/start"));
        var old = test.BookButton;
        await test.Dispatcher.DispatchAsync(Message(command));
        await test.Dispatcher.DispatchAsync(Callback(old));
        Assert.Equal(2, test.Sender.SentMenus.Count);
        Assert.NotNull(Assert.Single(test.Sender.CallbackAnswers).Text);
    }

    [Fact]
    public async Task CommandForAnotherBotAndUnsupportedUpdatesAreIgnored()
    {
        var test = new Harness();
        Assert.Equal(TelegramDispatchResult.Ignored, await test.Dispatcher.DispatchAsync(Message("/start@other_bot")));
        Assert.Equal(TelegramDispatchResult.Ignored, await test.Dispatcher.DispatchAsync(new Update()));
        Assert.Empty(test.Users.Stored);
    }

    [Fact]
    public async Task ConversationLeaseSerializesSameUserAndChatButAllowsOtherUsers()
    {
        var clock = new TestClock();
        var store = new ConversationStateStore(clock);
        using var first = await store.AcquireAsync(new(42, 100));
        var pending = store.AcquireAsync(new(42, 100));
        Assert.False(pending.IsCompleted);
        using var other = await store.AcquireAsync(new(43, 100)).WaitAsync(TimeSpan.FromSeconds(2));
        first.State.Selections["service"] = "haircut";
        Assert.Empty(other.State.Selections);
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("haircut", second.State.Selections["service"]);
    }

    [Fact]
    public async Task RealHostResolvesScopedDispatcherAndUsesRegisteredUserService()
    {
        var users = new UsersRepository();
        var staff = new StaffRepository();
        var sender = new FakeTelegramClient();
        await using var factory = new ApiFactory("Host=localhost;Database=not_opened;Username=test;Password=test");
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IUserRepository>(users);
            services.AddSingleton<IStaffRepository>(staff);
            services.AddSingleton<ITelegramMessageSender>(sender);
        }));
        using var client = configured.CreateClient();
        await using var scope = configured.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>().DispatchAsync(Message("/start"));
        Assert.Single(users.Stored);
        Assert.Single(sender.SentMenus);
    }

    [Fact]
    public async Task RegisteredRouteNotAllowedOnCurrentScreenCannotBeForged()
    {
        var calls = 0;
        var test = new Harness(extra: new Handler("staff", (_, _) => { calls++; return Task.CompletedTask; }));
        await test.Dispatcher.DispatchAsync(Message("/start"));
        var forged = test.BookButton[..test.BookButton.LastIndexOf(':')] + ":staff";
        await test.Dispatcher.DispatchAsync(Callback(forged));
        Assert.Equal(0, calls);
        Assert.NotNull(Assert.Single(test.Sender.CallbackAnswers).Text);
    }
    private static string Route(TelegramMenuButton button)
    {
        Assert.True(CallbackDataParser.TryParse(button.CallbackData, out var parsed));
        return parsed!.Route;
    }

    internal static Update Message(string text, long senderId = 42, long chatId = -999) => new()
    {
        Message = new Message
        {
            From = Sender(senderId), Chat = new Chat { Id = chatId, Type = ChatType.Private }, Text = text
        }
    };
    private static TelegramUser Sender(long id) => new() { Id = id, FirstName = "Ada", LastName = "Lovelace", Username = "ada" };
    private static Update Callback(string? data, long senderId = 42, long chatId = -999) => new()
    {
        CallbackQuery = new CallbackQuery
        {
            Id = "callback-1", From = Sender(senderId), ChatInstance = "test",
            Data = data, Message = new Message { Chat = new Chat { Id = chatId, Type = ChatType.Private } }
        }
    };

    private sealed class Harness
    {
        public readonly UsersRepository Users = new();
        public readonly StaffRepository Staff = new();
        public readonly FakeTelegramClient Sender = new();
        public readonly TestClock Clock = new();
        public readonly TelegramUpdateDispatcher Dispatcher;
        public string BookButton => Sender.SentMenus.Last().Buttons.First(button => Route(button) == "book").CallbackData;
        public Harness(bool owner = false, ITelegramHandler? extra = null)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Owner:TelegramUserId"] = owner ? "42" : "0" }).Build();
            var handlers = new List<ITelegramHandler> { new StartHandler(Sender, Staff, config, Clock) };
            if (extra is not null) handlers.Add(extra);
            Dispatcher = new TelegramUpdateDispatcher(handlers,
                new TelegramActorResolver(new UserService(Users, Clock)), Sender,
                new ConversationStateStore(Clock), Options.Create(new TelegramOptions()));
        }
    }

    private sealed class Handler(string route, Func<TelegramHandlerContext, CancellationToken, Task> action) : ITelegramHandler
    {
        public string Route => route;
        public Task HandleAsync(TelegramHandlerContext context, CancellationToken ct) => action(context, ct);
    }
    private sealed class TestClock : IClock { public DateTime UtcNow { get; set; } = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc); }
    private sealed class UsersRepository : IUserRepository
    {
        public List<AppUser> Stored { get; } = [];
        public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Stored.Find(user => user.Id == id));
        public Task<AppUser?> GetByTelegramUserIdAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(Stored.Find(user => user.TelegramUserId == id));
        public Task<AppUser> InsertOrGetAsync(AppUser candidate, CancellationToken cancellationToken = default)
        {
            var user = Stored.Find(user => user.TelegramUserId == candidate.TelegramUserId);
            if (user is null) Stored.Add(user = candidate);
            return Task.FromResult(user);
        }
        public void Add(AppUser user) => Stored.Add(user);
    }
    private sealed class StaffRepository : IStaffRepository
    {
        public StaffMember? Profile;
        public Task<StaffMember?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Profile?.UserId == userId ? Profile : null);
        public Task<StaffMember?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Profile?.Id == id ? Profile : null);
        public Task<IReadOnlyList<StaffMember>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Add(StaffMember staffMember) => Profile = staffMember;
    }
}
