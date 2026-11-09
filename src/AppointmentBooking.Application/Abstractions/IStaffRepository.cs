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

    Task<StaffMember?> GetByIdForUpdateAsync(Guid staffMemberId, CancellationToken ct);

    Task<StaffMember?> GetByIdSnapshotAsync(Guid staffMemberId, CancellationToken ct);

    Task CreateWithCoordinationAsync(StaffMember staffMember, CancellationToken ct);

    void Add(StaffMember staffMember);
}
