using AppointmentBooking.Application.Business;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed record BusinessSettingsEditDraft(BusinessSettingsView? Saved = null);
