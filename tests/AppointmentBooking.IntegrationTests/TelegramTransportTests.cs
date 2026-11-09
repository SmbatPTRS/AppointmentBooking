using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AppointmentBooking.IntegrationTests;

// Real Telegram library and dispatcher, with in-memory HTTP and user persistence adapters.
public sealed class TelegramTransportTests
{
    [Theory]
    [InlineData(false, "Polling")]
    [InlineData(true, "Webhook")]
    public async Task InactiveModeDoesNotResolveClientOrReceive(bool enabled, string mode)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var polling = new TelegramPollingService(services, services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TelegramOptions { Enabled = enabled, Mode = mode }),
            NullLogger<TelegramPollingService>.Instance);
        await polling.StartAsync(default);
        await polling.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        await polling.StopAsync(default);
    }

    [Fact]
    public async Task ProcessesInOrderAndAcknowledgesOnlyAfterFreshScopesAreDisposed()
    {
        var finished = Signal();
        var polls = 0;
        int? acknowledged = null;
        var webhookDeleted = false;
        await using var test = new Harness(async (request, ct) =>
        {
            using var json = await Body(request, ct);
            if (Is(request, "deleteWebhook"))
            {
                webhookDeleted = true;
                Assert.False(json.RootElement.TryGetProperty("drop_pending_updates", out var drop) && drop.GetBoolean());
                return Ok();
            }
            Assert.True(webhookDeleted);
            if (++polls == 1) return Updates(2, 1);
            acknowledged = json.RootElement.GetProperty("offset").GetInt32();
            finished.SetResult();
            return await WaitForShutdown(ct);
        });
        await test.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.Equal(3, acknowledged);
        Assert.Equal(new[] { 1, 2 }, test.Calls.Select(call => call.UpdateId));
        var contexts = test.Calls.Select(call => call.Context).ToArray();
        Assert.NotSame(contexts[0], contexts[1]);
        Assert.All(contexts, context => Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker));
        Assert.Single(test.Users.Stored);
    }

    [Fact]
    public async Task FailedEarlierUpdateIsRetriedWithDelayBeforeLaterUpdate()
    {
        var finished = Signal();
        var polls = 0;
        var attempts = 0;
        var offsets = new List<int?>();
        long failedAt = 0;
        TimeSpan retryDelay = default;
        await using var test = new Harness(async (request, ct) =>
        {
            if (Is(request, "deleteWebhook")) return Ok();
            using var json = await Body(request, ct);
            offsets.Add(json.RootElement.TryGetProperty("offset", out var offset) ? offset.GetInt32() : null);
            if (++polls == 1) return Updates(10, 11);
            if (polls == 2)
            {
                retryDelay = Stopwatch.GetElapsedTime(failedAt);
                return Updates(10, 11);
            }
            finished.SetResult();
            return await WaitForShutdown(ct);
        }, (context, _) =>
        {
            if (Id(context) == 10 && ++attempts == 1)
            {
                failedAt = Stopwatch.GetTimestamp();
                throw new InvalidOperationException("Simulated temporary processing failure");
            }
            return Task.CompletedTask;
        });
        await test.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.Equal(new[] { 10, 10, 11 }, test.Calls.Select(call => call.UpdateId));
        Assert.Equal(new int?[] { null, null, 12 }, offsets);
        Assert.True(retryDelay >= TimeSpan.FromMilliseconds(800));
        Assert.Equal(3, test.Calls.Select(call => call.Context.ContextId.InstanceId).Distinct().Count());
    }

    [Fact]
    public async Task SendRetryKeepsTheConfirmationIdentityAndRegisteredUser()
    {
        var finished = Signal();
        var polls = 0;
        var intents = new List<Guid>();
        Harness? test = null;
        await using var owned = test = new Harness(async (request, ct) =>
        {
            if (Is(request, "deleteWebhook")) return Ok();
            if (++polls <= 2) return Updates(20);
            finished.SetResult();
            return await WaitForShutdown(ct);
        }, async (context, ct) =>
        {
            context.State.ConfirmationRequestId ??= Guid.NewGuid();
            intents.Add(context.State.ConfirmationRequestId.Value);
            if (intents.Count == 1) test!.Sender.FailNextSend(new HttpRequestException("Simulated send failure"));
            await test!.Sender.SendTextAsync(context.ChatId, "Result", ct);
        });
        await test.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.Equal(2, intents.Count);
        Assert.Equal(intents[0], intents[1]);
        Assert.Single(test.Users.Stored);
    }

    [Fact]
    public async Task BusinessRejectionCountsAsHandledAndDoesNotBlockNextUpdate()
    {
        var finished = Signal();
        var polls = 0;
        int? nextOffset = null;
        await using var test = new Harness(async (request, ct) =>
        {
            if (Is(request, "deleteWebhook")) return Ok();
            if (++polls == 1) return Updates(30, 31);
            using var json = await Body(request, ct);
            nextOffset = json.RootElement.GetProperty("offset").GetInt32();
            finished.SetResult();
            return await WaitForShutdown(ct);
        }, (context, _) => Id(context) == 30
            ? throw new AppException(ErrorCodes.Forbidden, "Rejected", AppErrorKind.Forbidden)
            : Task.CompletedTask);
        await test.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.Equal(32, nextOffset);
        Assert.Equal(new[] { 30, 31 }, test.Calls.Select(call => call.UpdateId));
        Assert.Contains(test.Sender.SentMessages, message => message.Text.Contains("permission"));
    }

    [Fact]
    public async Task WebhookRemovalFailureIsRetriedBeforeReceiving()
    {
        var receiving = Signal();
        var deletes = 0;
        var requests = new List<string>();
        await using var test = new Harness(async (request, ct) =>
        {
            if (Is(request, "deleteWebhook"))
            {
                requests.Add("delete");
                if (++deletes == 1) throw new HttpRequestException("Temporary failure");
                return Ok();
            }
            requests.Add("get");
            receiving.SetResult();
            return await WaitForShutdown(ct);
        });
        await test.Start();
        await receiving.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.Equal(new[] { "delete", "delete", "get" }, requests);
        Assert.Empty(test.Calls);
    }

    [Fact]
    public async Task ShutdownCancelsAnActiveHandlerAndDisposesItsScope()
    {
        var handling = Signal();
        await using var test = new Harness((request, _) => Task.FromResult(Is(request, "deleteWebhook") ? Ok() : Updates(40, 41)),
            async (_, ct) => { handling.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        await test.Start();
        await handling.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        var call = Assert.Single(test.Calls);
        Assert.Equal(40, call.UpdateId);
        Assert.Throws<ObjectDisposedException>(() => call.Context.ChangeTracker);
    }

    [Fact]
    public async Task EmptyResponsesDoNotCauseABusyLoop()
    {
        var finished = Signal();
        var polls = 0;
        long first = 0;
        TimeSpan interval = default;
        await using var test = new Harness(async (request, ct) =>
        {
            if (Is(request, "deleteWebhook")) return Ok();
            if (++polls == 1) { first = Stopwatch.GetTimestamp(); return Updates(); }
            interval = Stopwatch.GetElapsedTime(first);
            finished.SetResult();
            return await WaitForShutdown(ct);
        });
        await test.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.True(interval >= TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task TelegramRateLimitWaitIsRespectedBeforeTheNextReceive()
    {
        var receiving = Signal();
        var polls = 0;
        long limitedAt = 0;
        TimeSpan interval = default;
        await using var test = new Harness(async (request, ct) =>
        {
            if (Is(request, "deleteWebhook")) return Ok();
            if (++polls == 1)
            {
                limitedAt = Stopwatch.GetTimestamp();
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("{\"ok\":false,\"error_code\":429,\"description\":\"retry later\",\"parameters\":{\"retry_after\":2}}", Encoding.UTF8, "application/json") };
            }
            interval = Stopwatch.GetElapsedTime(limitedAt);
            receiving.SetResult();
            return await WaitForShutdown(ct);
        });
        await test.Start();
        await receiving.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Stop();
        Assert.True(interval >= TimeSpan.FromMilliseconds(1800));
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int Id(TelegramHandlerContext context) => int.Parse(context.Text!.Split(' ')[1]);
    private static bool Is(HttpRequestMessage request, string method) => request.RequestUri!.AbsolutePath.EndsWith("/" + method, StringComparison.Ordinal);
    private static async Task<JsonDocument> Body(HttpRequestMessage request, CancellationToken ct) =>
        JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
    private static HttpResponseMessage Ok() => Json("{\"ok\":true,\"result\":true}");
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Updates(params int[] ids) => Json(JsonSerializer.Serialize(new
    {
        ok = true,
        result = ids.Select(id => new
        {
            update_id = id,
            message = new { message_id = id, date = 0, text = "/probe " + id,
                @from = new { id = 42, is_bot = false, first_name = "Ada" }, chat = new { id = 42, type = "private" } }
        })
    }));
    private static async Task<HttpResponseMessage> WaitForShutdown(CancellationToken ct)
    { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException("Expected cancellation"); }

    private sealed record Call(int UpdateId, AppDbContext Context);
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HttpClient http;
        public readonly FakeTelegramClient Sender = new();
        public readonly UsersRepository Users = new();
        public readonly ConcurrentQueue<Call> Calls = new();
        public readonly TelegramPollingService Polling;
        public Harness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            Func<TelegramHandlerContext, CancellationToken, Task>? action = null)
        {
            http = new HttpClient(new HttpStub(respond));
            var services = new ServiceCollection();
            var options = Options.Create(new TelegramOptions { Enabled = true, Mode = "Polling", PollingRetryDelaySeconds = 1 });
            services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(new TelegramBotClientOptions(
                "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345") { RetryCount = 0 }, http));
            services.AddDbContext<AppDbContext>(builder => builder.UseNpgsql("Host=localhost;Database=not_opened;Username=test;Password=test"));
            services.AddSingleton<IUserRepository>(Users);
            services.AddSingleton<IClock, Clock>();
            services.AddSingleton<ConversationStateStore>();
            services.AddScoped<UserService>();
            services.AddScoped<TelegramActorResolver>();
            services.AddScoped<ProbeHandler>(p => new ProbeHandler(p.GetRequiredService<AppDbContext>(), Calls, action));
            services.AddScoped<TelegramUpdateDispatcher>(p => new TelegramUpdateDispatcher(
                [p.GetRequiredService<ProbeHandler>()], p.GetRequiredService<TelegramActorResolver>(), Sender,
                p.GetRequiredService<ConversationStateStore>(), options));
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Polling = new TelegramPollingService(provider, provider.GetRequiredService<IServiceScopeFactory>(), options,
                NullLogger<TelegramPollingService>.Instance);
        }
        public Task Start() => Polling.StartAsync(default);
        public async Task Stop()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Polling.StopAsync(deadline.Token);
            if (Polling.ExecuteTask is { } execution) await execution.WaitAsync(TimeSpan.FromSeconds(2));
        }
        public async ValueTask DisposeAsync()
        { await Stop(); Polling.Dispose(); await provider.DisposeAsync(); http.Dispose(); }
    }
    private sealed class ProbeHandler(AppDbContext db, ConcurrentQueue<Call> calls,
        Func<TelegramHandlerContext, CancellationToken, Task>? action) : ITelegramHandler
    {
        public string Route => "probe";
        public Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
        { calls.Enqueue(new Call(Id(context), db)); return action?.Invoke(context, ct) ?? Task.CompletedTask; }
    }
    private sealed class HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private sealed class Clock : IClock { public DateTime UtcNow => new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc); }
    public sealed class UsersRepository : IUserRepository
    {
        public readonly List<User> Stored = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Stored.Find(user => user.Id == id));
        public Task<User?> GetByTelegramUserIdAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(Stored.Find(user => user.TelegramUserId == id));
        public Task<User> InsertOrGetAsync(User candidate, CancellationToken cancellationToken = default)
        { var user = Stored.Find(u => u.TelegramUserId == candidate.TelegramUserId); if (user is null) Stored.Add(user = candidate); return Task.FromResult(user); }
        public void Add(User user) => Stored.Add(user);
    }
}
