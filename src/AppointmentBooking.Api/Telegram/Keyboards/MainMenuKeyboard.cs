using AppointmentBooking.Api.Telegram.State;

namespace AppointmentBooking.Api.Telegram.Keyboards;

public static class MainMenuKeyboard
{
    public static IReadOnlyList<TelegramMenuButton> Create(ConversationState state,
        bool owner, bool activeStaff, bool anyStaff)
    {
        var routes = new List<(string Label, string Route)> { ("Book an appointment", "book"), ("My bookings", "bookings") };
        routes.AddRange(ManagementKeyboard.ForRole(owner, activeStaff, anyStaff));
        state.SetAllowedCallbacks(routes.Select(item => item.Route));
        return routes.Select(item => new TelegramMenuButton(item.Label,
            CallbackDataParser.Format(item.Route, state.Revision))).ToArray();
    }
}
