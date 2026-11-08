using System.Net;
using System.Text;
using System.Text.Json;
using AppointmentBooking.Api.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace AppointmentBooking.IntegrationTests;

public sealed class TelegramMessageSenderTests
{
    // Deliberately fake token. Every HTTP request is intercepted in memory.
    private const string TestToken = "123456789:TEST_ONLY_NOT_A_REAL_BOT_TOKEN_12345";

    [Theory]
    [InlineData(5_000_000_000)]
    [InlineData(-1_000_000_000_001)]
    public async Task SendsPlainTextToTheRequestedChat(long chatId)
    {
        var requests = 0;
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(chatId.ToString(), body.RootElement.GetProperty("chat_id").ToString());
            Assert.Equal("Welcome! Բարև <b>plain text</b>", body.RootElement.GetProperty("text").GetString());
            Assert.False(body.RootElement.TryGetProperty("parse_mode", out _));
            return JsonResponse(HttpStatusCode.OK,
                "{\"ok\":true,\"result\":{\"message_id\":1,\"date\":0,\"chat\":{\"id\":123,\"type\":\"private\"}}}");
        });
        using var http = new HttpClient(handler);
        ITelegramMessageSender sender = new TelegramMessageSender(new TelegramBotClient(TestToken, http));

        await sender.SendTextAsync(chatId, "Welcome! Բարև <b>plain text</b>");

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task TelegramRejectionReachesTheCaller()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.BadRequest,
            "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}")));
        using var http = new HttpClient(handler);
        var sender = new TelegramMessageSender(new TelegramBotClient(TestToken, http));

        var error = await Assert.ThrowsAsync<ApiRequestException>(() => sender.SendTextAsync(123, "Welcome!"));

        Assert.Equal(400, error.ErrorCode);
    }

    [Fact]
    public async Task CancellationBeforeSendingMakesNoRequest()
    {
        var requests = 0;
        using var handler = new StubHttpHandler((_, _) =>
        {
            requests++;
            throw new InvalidOperationException("A cancelled send must not make a request.");
        });
        using var http = new HttpClient(handler);
        var sender = new TelegramMessageSender(new TelegramBotClient(TestToken, http));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendTextAsync(123, "Welcome!", cancellation.Token));

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task CancellationStopsAnInFlightRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Request should have been cancelled.");
        });
        using var http = new HttpClient(handler);
        var sender = new TelegramMessageSender(new TelegramBotClient(TestToken, http));
        using var cancellation = new CancellationTokenSource();

        var sending = sender.SendTextAsync(123, "Welcome!", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SendsMenuButtonsAndAcknowledgesCallbacksThroughLibrary()
    {
        var requests = new List<string>();
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (path.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                var button = body.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0];
                Assert.Equal("Book", button.GetProperty("text").GetString());
                Assert.Equal("button-data", button.GetProperty("callback_data").GetString());
                return JsonResponse(HttpStatusCode.OK,
                    "{\"ok\":true,\"result\":{\"message_id\":1,\"date\":0,\"chat\":{\"id\":123,\"type\":\"private\"}}}");
            }
            Assert.EndsWith("/answerCallbackQuery", path);
            Assert.Equal("callback-id", body.RootElement.GetProperty("callback_query_id").GetString());
            Assert.Equal("Old button", body.RootElement.GetProperty("text").GetString());
            return JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"result\":true}");
        });
        using var http = new HttpClient(handler);
        var sender = new TelegramMessageSender(new TelegramBotClient(TestToken, http));
        await sender.SendMenuAsync(123, "Menu", [new TelegramMenuButton("Book", "button-data")]);
        await sender.AnswerCallbackAsync("callback-id", "Old button");
        Assert.Equal(2, requests.Count);
    }
    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
