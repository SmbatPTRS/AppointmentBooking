using AppointmentBooking.Api.Telegram.State;

namespace AppointmentBooking.Api.Telegram;

public sealed record TelegramHandlerContext(
    TelegramActor Caller, long ChatId, string? Text, TelegramCallback? Callback,
    ConversationState State, Guid? ItemId = null, bool IsNavigation = false, bool IsRetry = false);

/// <summary>Register a scoped implementation for one route, e.g. book or schedule.</summary>
public interface ITelegramHandler
{
    string Route { get; }
    Task HandleAsync(TelegramHandlerContext context, CancellationToken ct);
}

public enum TelegramDispatchResult { Ignored, Handled }
