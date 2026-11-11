using AppointmentBooking.Api.Telegram.State;

namespace AppointmentBooking.Api.Telegram.Keyboards;

public static class NavigationKeyboard
{
    public static IReadOnlyList<TelegramMenuButton> Create(ConversationState state) =>
        [state.Button("Back", "back"), state.Button("Cancel", "cancel")];
}
