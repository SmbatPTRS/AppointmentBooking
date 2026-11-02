using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface IStaffRepository
{
    Task<StaffMember?> GetByIdAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken = default);

    Task<StaffMember?> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StaffMember>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    void Add(StaffMember staffMember);
}