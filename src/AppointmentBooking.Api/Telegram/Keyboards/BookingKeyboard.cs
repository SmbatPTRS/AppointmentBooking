using AppointmentBooking.Api.Telegram.State;

namespace AppointmentBooking.Api.Telegram.Keyboards;

/// <summary>Builds the menus used by customer booking and appointment screens.</summary>
public static class BookingKeyboard
{
    /// <summary>Creates a server-bound item menu and appends shared navigation.</summary>
    public static IReadOnlyList<TelegramMenuButton> Items<T>(
        ConversationState state, IEnumerable<T> items, Func<T, string> label,
        Func<T, Guid> id, string route)
    {
        var buttons = items.Select(item => state.ItemButton(label(item), route, id(item))).ToList();
        buttons.AddRange(NavigationKeyboard.Create(state));
        return buttons;
    }

    /// <summary>Creates a confirmation menu for the currently reviewed booking.</summary>
    public static IReadOnlyList<TelegramMenuButton> Confirmation(ConversationState state) =>
    [
        state.Button("Confirm booking", "book.confirm"),
        ..NavigationKeyboard.Create(state)
    ];
}
