using System.ComponentModel.DataAnnotations;

namespace AppointmentBooking.Application.Catalog;

public sealed record CreateServiceRequest(
    [property: Required] string Name);

public sealed record UpdateServiceRequest(
    [property: Required] string Name,
    bool IsActive);

public sealed record ConfigureStaffServiceRequest(
    [property: Range(typeof(decimal), "0", "9999999999.99", ParseLimitsInInvariantCulture = true)]
    decimal Price,
    [property: Range(1, int.MaxValue)] int DurationMinutes,
    bool IsActive);

public sealed record ServiceDto(Guid Id, string Name, bool IsActive);

public sealed record StaffServiceDto(
    Guid Id,
    Guid StaffMemberId,
    Guid ServiceId,
    string ServiceName,
    decimal Price,
    int DurationMinutes,
    bool IsActive);
