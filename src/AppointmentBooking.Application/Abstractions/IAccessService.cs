using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Application.Abstractions;

/// <summary>
/// Shared permission contract. A implements it in Task 03; no production
/// implementation is registered yet. Actor must come from a trusted identity adapter.
/// Methods complete without a value on success and throw AppException on rejection.
/// See docs/access-contract.md for error precedence and exact Code/Kind pairs.
/// </summary>
public interface IAccessService
{
    /// <summary>
    /// Allows only the configured owner. Other existing users receive
    /// ErrorCodes.Forbidden / AppErrorKind.Forbidden.
    /// </summary>
    Task RequireOwnerAsync(Actor actor, CancellationToken ct);

    /// <summary>
    /// Allows the owner for any existing staff profile (including inactive),
    /// or the linked user for their own active profile. Missing staff:
    /// StaffNotFound/NotFound; other user's profile: Forbidden/Forbidden;
    /// own inactive profile: StaffInactive/Forbidden.
    /// </summary>
    Task RequireStaffManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct);

    /// <summary>
    /// Allows the owner or linked user to manage existing appointments even
    /// when the staff profile is inactive. Missing staff: StaffNotFound/NotFound;
    /// other user's profile: Forbidden/Forbidden. Completion timing and booking
    /// ownership/lifecycle checks remain the booking service's responsibility.
    /// </summary>
    Task RequireAppointmentManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct);
}
