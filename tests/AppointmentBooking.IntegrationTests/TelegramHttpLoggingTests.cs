using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AppointmentBooking.Api.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace AppointmentBooking.IntegrationTests;

public sealed class TelegramHttpLoggingTests
{
    private const string FakeToken = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredTelegramClientDoesNotLogTokenOnSuccessOrRejection(bool reject)
    {
        var logs = new CapturedLogs();
        var calls = 0;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:Enabled"] = "true", ["Telegram:Mode"] = "Polling", ["Telegram:BotToken"] = FakeToken
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IConfiguration>(config);
        services.AddTelegramInfrastructure();
        // Use the registered client and HTTP pipeline, but never contact Telegram.
        services.AddHttpClient("TelegramBot").ConfigurePrimaryHttpMessageHandler(() => new HttpStub(request =>
        {
            calls++;
            Assert.Equal("/bot" + FakeToken + "/sendMessage", request.RequestUri!.AbsolutePath);
            return reject
                ? Json(HttpStatusCode.BadRequest, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}")
                : Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":1,\"date\":0,\"chat\":{\"id\":42,\"type\":\"private\"}}}");
        }));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ITelegramBotClient>();
        if (reject)
            await Assert.ThrowsAsync<ApiRequestException>(() => client.SendMessage(42, "Test reply"));
        else
            await client.SendMessage(42, "Test reply");

        // Prove the sink is active so an empty or disabled capture cannot make the test pass.
        provider.GetRequiredService<ILogger<TelegramHttpLoggingTests>>().LogInformation("Capture probe");
        Assert.Contains(logs.Messages, message => message.Contains("Capture probe", StringComparison.Ordinal));
        Assert.Equal(1, calls);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(FakeToken, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("/bot" + FakeToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultLoggingControlShowsThatRequestPathWouldBeCaptured()
    {
        const string marker = "not_a_secret_path_marker";
        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddHttpClient("DefaultLoggingControl").ConfigurePrimaryHttpMessageHandler(() =>
            new HttpStub(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("DefaultLoggingControl");
        using var response = await http.GetAsync("https://example.invalid/" + marker);
        response.EnsureSuccessStatusCode();
        Assert.Contains(logs.Messages, message => message.Contains(marker, StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class HttpStub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Messages);
        public void Dispose() { }
        private sealed class Capture(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(category + ": " + formatter(state, exception));
        }
    }
}
