using AppointmentBooking.Api.Telegram.State;

namespace AppointmentBooking.Api.Telegram.Keyboards;

public static class ManagementKeyboard
{
    public static IEnumerable<(string Label, string Route)> ForRole(bool owner, bool activeStaff, bool anyStaff)
    {
        if (owner || anyStaff) yield return ("Staff appointments", "appointments");
        if (owner || activeStaff)
        {
            yield return ("Offerings", "catalog");
            yield return ("Schedule", "schedule");
            yield return ("Display name", "profile");
        }
        if (owner)
        {
            yield return ("Staff management", "staff");
            yield return ("Shared services", "services");
            yield return ("Business settings", "settings");
        }
    }
}
