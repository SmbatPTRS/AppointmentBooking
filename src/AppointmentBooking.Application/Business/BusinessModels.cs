namespace AppointmentBooking.Application.Business;

public sealed record BusinessSettingsView(
    string DisplayName, string StaffLabel, string TimeZoneId, string CurrencyCode);
