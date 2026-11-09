using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Staff;

public sealed record StaffProfile(Guid Id, Guid UserId, string DisplayName, bool IsActive)
{
    internal static StaffProfile From(StaffMember staff) =>
        new(staff.Id, staff.UserId, staff.DisplayName, staff.IsActive);
}
