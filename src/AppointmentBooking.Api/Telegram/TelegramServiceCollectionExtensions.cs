using AppointmentBooking.Api.Configuration;
using AppointmentBooking.Api.Telegram.Handlers;
using AppointmentBooking.Api.Telegram.State;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AppointmentBooking.Api.Telegram;

public static class TelegramServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();
        services.AddOptions<TelegramOptions>().BindConfiguration("Telegram").ValidateOnStart();
        // The Bot API token is part of the URI path. Keep default HTTP loggers from recording it.
        services.AddHttpClient("TelegramBot").RemoveAllLoggers();
        services.AddSingleton<ITelegramBotClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TelegramOptions>>().Value;
            if (!options.Enabled) throw new InvalidOperationException("Telegram is disabled.");
            return new TelegramBotClient(new TelegramBotClientOptions(options.BotToken)
            {
                // Transport code owns retries; never hide a send failure behind automatic retries.
                RetryCount = 0
            }, provider.GetRequiredService<IHttpClientFactory>().CreateClient("TelegramBot"));
        });
        services.AddSingleton<ITelegramMessageSender, TelegramMessageSender>();
        services.AddSingleton<ConversationStateStore>();
        services.AddScoped<TelegramActorResolver>();
        services.AddScoped<TelegramUpdateDispatcher>();
        services.AddScoped<ITelegramHandler, StartHandler>();
        return services;
    }
}
