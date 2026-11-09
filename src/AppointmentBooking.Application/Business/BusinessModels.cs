using System.Text.Json.Serialization;

namespace AppointmentBooking.Application.Business;

public sealed record BusinessSettingsView(
    string DisplayName, string StaffLabel, string TimeZoneId, string CurrencyCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateBusinessLabelsRequest(
    string DisplayName, string StaffLabel);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateBusinessTimeZoneAndCurrencyRequest(
    string TimeZoneId, string CurrencyCode);
