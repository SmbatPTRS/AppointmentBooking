using AppointmentBooking.Api.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace AppointmentBooking.Api.Telegram;

/// <summary>One sequential receiver for a single application instance.</summary>
public sealed class TelegramPollingService(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || settings.Mode != "Polling") return;

        // Do not block host startup on a Telegram network request (.NET 8 BackgroundService).
        await Task.Yield();
        var client = services.GetRequiredService<ITelegramBotClient>();
        var webhookRemoved = false;
        int? offset = null;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!webhookRemoved)
                {
                    await client.DeleteWebhook(dropPendingUpdates: false, cancellationToken: stoppingToken);
                    webhookRemoved = true;
                }
                var updates = await client.GetUpdates(offset: offset, timeout: settings.PollingTimeoutSeconds,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: stoppingToken);
                foreach (var update in updates.OrderBy(update => update.Id))
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    if (offset.HasValue && update.Id < offset.Value) continue;
                    // Dispose failed/finished database work before a new operation or retry begins.
                    await using (var scope = scopeFactory.CreateAsyncScope())
                    {
                        await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>()
                            .DispatchAsync(update, stoppingToken);
                    }
                    // Only successful dispatch AND disposal advance the acknowledgement boundary.
                    offset = checked(update.Id + 1);
                }
                failures = 0;
                if (updates.Length == 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ApiRequestException error) when (error.ErrorCode is 401 or 409)
            {
                // Wrong credentials / another receiver need operator action, not an endless retry.
                // Do not log the request URL, token, payload or raw exception.
                logger.LogCritical("Telegram polling stopped: authentication or receiver conflict (code {Code}).", error.ErrorCode);
                throw new InvalidOperationException("Telegram polling authentication or receiver configuration needs attention.");
            }
            catch (Exception error)
            {
                failures = Math.Min(failures + 1, 20);
                var seconds = Math.Min(settings.PollingMaxRetryDelaySeconds,
                    settings.PollingRetryDelaySeconds * Math.Pow(2, failures - 1));
                if (error is ApiRequestException api && api.Parameters?.RetryAfter is > 0)
                    seconds = Math.Max(seconds, api.Parameters.RetryAfter.Value);
                logger.LogWarning("Telegram update processing failed ({FailureType}); retrying after {DelaySeconds}s.",
                    error.GetType().Name, seconds);
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
