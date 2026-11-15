using AppointmentBooking.Api.Telegram.State;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Api.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace AppointmentBooking.Api.Telegram;

/// <summary>Scoped: both transports must await this dispatcher within a fresh operation scope.</summary>
public sealed class TelegramUpdateDispatcher
{
    private readonly Dictionary<string, ITelegramHandler> handlers;
    private readonly TelegramActorResolver actors;
    private readonly ITelegramMessageSender sender;
    private readonly ConversationStateStore states;
    private readonly TelegramOptions options;

    public TelegramUpdateDispatcher(IEnumerable<ITelegramHandler> handlers, TelegramActorResolver actors,
        ITelegramMessageSender sender, ConversationStateStore states, IOptions<TelegramOptions> options)
    {
        this.handlers = handlers.ToDictionary(handler => handler.Route, StringComparer.Ordinal);
        if (this.handlers.Keys.Any(route => !CallbackDataParser.IsValidRoute(route) || route is "back" or "cancel"))
            throw new InvalidOperationException("Invalid Telegram handler route.");
        this.actors = actors;
        this.sender = sender;
        this.states = states;
        this.options = options.Value;
    }

    public async Task<TelegramDispatchResult> DispatchAsync(Update update, CancellationToken ct = default)
    {
        try
        {
            return await DispatchCoreAsync(update, ct);
        }
        catch (TelegramCallbackExpiredException) when (update.CallbackQuery is not null && !ct.IsCancellationRequested)
        {
            // The old click cannot be acknowledged again. Do not block fresh updates behind it.
            return TelegramDispatchResult.Handled;
        }
    }

    private async Task<TelegramDispatchResult> DispatchCoreAsync(Update update, CancellationToken ct)
    {
        var callback = update.CallbackQuery;
        var telegramUser = callback?.From ?? update.Message?.From;
        var chat = callback?.Message?.Chat ?? update.Message?.Chat;
        if (telegramUser is null || telegramUser.IsBot || telegramUser.Id <= 0 || chat is null)
        {
            if (callback is not null) await sender.AnswerCallbackAsync(callback.Id, "Open the bot and use /start.", ct);
            return TelegramDispatchResult.Ignored;
        }

        TelegramCallback? parsed = null;
        if (callback is not null && !CallbackDataParser.TryParse(callback.Data, out parsed))
        {
            await sender.AnswerCallbackAsync(callback.Id, "Invalid button. Use /start.", ct);
            return TelegramDispatchResult.Handled;
        }

        using var lease = await states.AcquireAsync(new ConversationKey(telegramUser.Id, chat.Id), ct);
        var state = lease.State;
        // A send retry for Back must render the same restored screen, not pop history again.
        var deliveryKey = $"{update.Id}|{callback?.Id}|{callback?.Data}|{update.Message?.Text}";
        var resumingNavigation = state.PendingNavigation?.Key == deliveryKey;
        var resumingDispatch = state.PendingDispatch?.Key == deliveryKey;
        var dispatchSourceRoute = resumingDispatch
            ? state.PendingDispatch!.Value.SourceRoute
            : (string?)null;
        var retryRender = resumingDispatch &&
            state.PendingDispatch!.Value.SourceRoute != state.PendingDispatch.Value.DestinationRoute;
        if (!resumingNavigation) state.PendingNavigation = null;
        if (!resumingDispatch) state.PendingDispatch = null;
        Guid? itemId = null;
        string route;
        if (parsed is not null)
        {
            var itemValid = parsed.ButtonKey is null
                ? state.AllowsPlainCallback(parsed.Route)
                : state.TryResolveItem(parsed, out _);
            if (!resumingNavigation && !resumingDispatch &&
                (parsed.Revision != state.Revision || !state.AllowsCallback(parsed.Route) || !itemValid))
            {
                await sender.AnswerCallbackAsync(callback!.Id, "This screen expired or changed. Use /start.", ct);
                return TelegramDispatchResult.Handled;
            }
            if (parsed.ButtonKey is not null && state.TryResolveItem(parsed, out var selected)) itemId = selected;
            route = parsed.Route;
        }
        else
        {
            var text = update.Message?.Text;
            if (text is null) return TelegramDispatchResult.Ignored;
            if (text.StartsWith('/'))
            {
                var command = text.Split(' ', 2)[0][1..].Split('@', 2);
                if (command.Length == 2 && (string.IsNullOrWhiteSpace(options.BotUsername) ||
                    !string.Equals(command[1], options.BotUsername, StringComparison.OrdinalIgnoreCase)))
                    return TelegramDispatchResult.Ignored;
                route = command[0];
            }
            else route = state.Step;
        }

        if (resumingDispatch)
        {
            route = state.PendingDispatch!.Value.DestinationRoute;
            itemId = state.PendingDispatch.Value.ItemId;
        }

        var navigating = resumingNavigation || route is "back" or "cancel" or "start";
        if (navigating)
        {
            if (callback is not null) await sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            if (!resumingNavigation)
            {
                if (route == "back") state.GoBack(states.UtcNow);
                else state.Reset(states.UtcNow);
                if (!handlers.ContainsKey(state.Step)) state.Reset(states.UtcNow);
                state.PendingNavigation = (deliveryKey, state.Step);
            }
            route = state.PendingNavigation!.Value.Route;
        }

        if (!handlers.TryGetValue(route, out var handler))
        {
            if (callback is not null) await sender.AnswerCallbackAsync(callback.Id, "This screen is not available yet.", ct);
            else await sender.SendTextAsync(chat.Id, "Use /start to open the main menu.", ct);
            return TelegramDispatchResult.Handled;
        }
        // Clear the button spinner before feature processing. Retryable errors still propagate.
        if (callback is not null && !navigating) await sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        var stepBeforeHandler = state.Step;

        var handlerStarted = false;
        try
        {
            var caller = await actors.ResolveAsync(telegramUser, ct);
            if (!navigating) state.PendingDispatch = (deliveryKey, route, route, itemId);
            handlerStarted = true;
            await handler.HandleAsync(new TelegramHandlerContext(caller, chat.Id,
                navigating ? null : update.Message?.Text, navigating ? null : parsed, state,
                navigating ? null : itemId, navigating, resumingDispatch, retryRender), ct);
        }
        catch (AppException error)
        {
            // A known business rejection is handled. Unexpected failures and failed sends are not swallowed.
            var message = error.Kind == AppErrorKind.Forbidden
                ? "You do not have permission for this action."
                : "This action could not be completed. Use /start to return to the menu.";
            await sender.SendTextAsync(chat.Id, message, ct);
        }
        catch when (handlerStarted)
        {
            if (navigating)
            {
                state.PendingNavigation = (deliveryKey, route);
            }
            else
            {
                var destinationRoute = state.Step != stepBeforeHandler
                    ? state.Step
                    : route;

                state.PendingDispatch = (
                    deliveryKey,
                    dispatchSourceRoute ?? route,
                    destinationRoute,
                    itemId);
            }

            throw;
        }
        if (navigating) state.PendingNavigation = null;
        state.PendingDispatch = null;
        return TelegramDispatchResult.Handled;
    }
}
