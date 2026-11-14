using System.Text.Json.Serialization;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Staff;

public sealed record StaffProfile(Guid Id, Guid UserId, string DisplayName, bool IsActive)
{
    internal static StaffProfile From(StaffMember staff) =>
        new(staff.Id, staff.UserId, staff.DisplayName, staff.IsActive);
}

public sealed record StaffListItem(Guid Id, string DisplayName, bool IsActive);

public sealed record RegisteredStaffCandidate(
    Guid Id, long TelegramUserId, string DisplayName, string? TelegramUsername);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateStaffRequest(
    [property: JsonRequired] Guid UserId,
    string DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameStaffRequest(string DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetStaffActivationRequest([property: JsonRequired] bool IsActive);
