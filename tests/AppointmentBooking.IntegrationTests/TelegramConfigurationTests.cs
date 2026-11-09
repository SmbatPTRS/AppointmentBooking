using Microsoft.Extensions.Logging;
using System.Net;
using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram;
using AppointmentBooking.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AppointmentBooking.IntegrationTests;

public sealed class TelegramConfigurationTests
{
    [Fact]
    public void DisabledTelegramRequiresNoTokenAndCreatesNoNetworkClientAtStartup()
    {
        var result = new TelegramOptionsValidator().Validate(null, new TelegramOptions());
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("Polling", "", "")]
    [InlineData("Neither", "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345", "")]
    [InlineData("Webhook", "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345", "http://example.com/hook")]
    [InlineData("Webhook", "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345", "https://localhost/hook")]
    public void EnabledTelegramRejectsInvalidConfigurationWithoutLeakingToken(string mode, string token, string url)
    {
        var result = new TelegramOptionsValidator().Validate(null, new TelegramOptions
        { Enabled = true, Mode = mode, BotToken = token, WebhookUrl = url });
        Assert.True(result.Failed);
        if (token.Length > 0) Assert.DoesNotContain(token, string.Join(" ", result.Failures));
    }

    [Theory]
    [InlineData(0, 1, 30)]
    [InlineData(51, 1, 30)]
    [InlineData(25, 0, 30)]
    [InlineData(25, 2, 1)]
    public void InvalidPollingWaitsAreRejected(int timeout, int retry, int maximum)
    {
        var result = new TelegramOptionsValidator().Validate(null, new TelegramOptions
        {
            Enabled = true, Mode = "Polling", BotToken = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345",
            PollingTimeoutSeconds = timeout, PollingRetryDelaySeconds = retry, PollingMaxRetryDelaySeconds = maximum
        });
        Assert.True(result.Failed);
    }
    [Fact]
    public void ClientIsReusedAcrossScopesWithoutSendingRequests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:Enabled"] = "true", ["Telegram:Mode"] = "Polling",
            ["Telegram:BotToken"] = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddTelegramInfrastructure();
        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<ITelegramBotClient>(),
            second.ServiceProvider.GetRequiredService<ITelegramBotClient>());
        Assert.IsType<TelegramMessageSender>(provider.GetRequiredService<ITelegramMessageSender>());
        Assert.IsType<TelegramPollingService>(Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()));
    }
    [Fact]
    public async Task TelegramHttpRequestsDoNotLogTheBotToken()
    {
        const string token = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345";
        var logs = new CapturedLogs();
        var calls = 0;
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTelegramInfrastructure();
        services.AddHttpClient("TelegramBot").ConfigurePrimaryHttpMessageHandler(() => new HttpStub(() => calls++));
        using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("TelegramBot");
        using var response = await http.GetAsync("https://api.telegram.org/bot" + token + "/getUpdates");
        response.EnsureSuccessStatusCode();
        Assert.Equal(1, calls);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(token, StringComparison.Ordinal));
    }

    private sealed class HttpStub(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { called(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
    private sealed class CapturedLogs : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
