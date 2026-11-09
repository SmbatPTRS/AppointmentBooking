namespace AppointmentBooking.Application.Common;

public static class ErrorCodes
{
    // General validation and access.
    public const string ValidationFailed = "validation.failed";
    public const string Unauthorized = "access.unauthorized";
    public const string Forbidden = "access.forbidden";

    // Users and staff.
    public const string UserNotFound = "user.not_found";
    public const string StaffNotFound = "staff.not_found";
    public const string StaffAlreadyExists = "staff.already_exists";
    public const string StaffInactive = "staff.inactive";

    // Business settings.
    public const string BusinessSettingsNotFound =
        "business_settings.not_found";

    public const string BusinessSettingsLocked =
        "business_settings.locked";

    public const string InvalidTimeZone =
        "business_settings.invalid_time_zone";

    public const string InvalidCurrency =
        "business_settings.invalid_currency";

    // Catalogue.
    public const string ServiceNotFound = "service.not_found";
    public const string ServiceInactive = "service.inactive";

    public const string StaffServiceNotFound =
        "staff_service.not_found";

    public const string StaffServiceInactive =
        "staff_service.inactive";

    public const string StaffServiceMismatch =
        "staff_service.staff_mismatch";

    // Scheduling.
    public const string InvalidWorkingHours =
        "schedule.invalid_working_hours";

    public const string InvalidDayOff =
        "schedule.invalid_day_off";

    public const string DayOffNotFound =
        "schedule.day_off_not_found";

    public const string DuplicateDayOff =
        "schedule.duplicate_day_off";

    public const string ScheduleConflictsWithBookings =
        "schedule.conflicts_with_bookings";

    // Booking.
    public const string BookingNotFound =
        "booking.not_found";
    
    public const string ConfigurationChanged =
        "booking.configuration_changed";

    public const string TimeUnavailable =
        "booking.time_unavailable";

    public const string BookingOutsideWindow =
        "booking.outside_window";

    public const string InvalidBookingTime =
        "booking.invalid_time";

    public const string OfferingChanged =
        "booking.offering_changed";
    
    public const string OfferingInactive =
        "booking.offering_inactive";

    public const string RequestIdConflict =
        "booking.request_id_conflict";

    public const string BookingCannotBeCancelled =
        "booking.cannot_be_cancelled";

    public const string BookingCannotBeCompleted =
        "booking.cannot_be_completed";
}