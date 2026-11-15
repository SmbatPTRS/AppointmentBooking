using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed record StaffManagementDraft(
    int Page = 0, Guid? StaffId = null, string? SavedName = null,
    bool? PendingActive = null, bool ActivationSaved = false,
    RegisteredStaffCandidate? Candidate = null, Guid? CreatedStaffId = null);
