using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.UnitTests;

public sealed class AccessServiceTests
{
    private readonly User owner = CreateUser(100);
    private readonly User employee = CreateUser(200);
    private readonly User otherEmployee = CreateUser(300);
    private readonly User client = CreateUser(400);
    private readonly StaffMember profile;
    private readonly AccessService access;

    public AccessServiceTests()
    {
        profile = new StaffMember(employee.Id, "Employee");
        var otherProfile = new StaffMember(otherEmployee.Id, "Other employee");
        var store = new TestStore([owner, employee, otherEmployee, client], [profile, otherProfile]);
        access = new AccessService(store, store, owner.TelegramUserId);
    }

    [Fact]
    public async Task OwnerNeedsNoStaffProfile()
    {
        await access.RequireOwnerAsync(new Actor(owner.Id), default);
        await AssertError(
            () => access.RequireOwnerAsync(new Actor(employee.Id), default),
            ErrorCodes.Forbidden, AppErrorKind.Forbidden);
    }

    [Fact]
    public async Task ActiveEmployeeCanManageOwnProfile()
    {
        await access.RequireStaffManagementAsync(new Actor(employee.Id), profile.Id, default);
    }

    [Fact]
    public async Task InactiveEmployeeCanManageExistingAppointmentsButCannotManageOfferings()
    {
        profile.Deactivate();

        await access.RequireAppointmentManagementAsync(new Actor(employee.Id), profile.Id, default);
        await AssertError(
            () => access.RequireStaffManagementAsync(new Actor(employee.Id), profile.Id, default),
            ErrorCodes.StaffInactive, AppErrorKind.Forbidden);
    }

    [Fact]
    public async Task OwnerCanManageInactiveProfile()
    {
        profile.Deactivate();

        await access.RequireStaffManagementAsync(new Actor(owner.Id), profile.Id, default);
        await access.RequireAppointmentManagementAsync(new Actor(owner.Id), profile.Id, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlinkedCallerIsForbiddenEvenWhenProfileIsInactive(bool callerIsEmployee)
    {
        profile.Deactivate();
        var actor = new Actor(callerIsEmployee ? otherEmployee.Id : client.Id);

        await AssertError(
            () => access.RequireStaffManagementAsync(actor, profile.Id, default),
            ErrorCodes.Forbidden, AppErrorKind.Forbidden);
        await AssertError(
            () => access.RequireAppointmentManagementAsync(actor, profile.Id, default),
            ErrorCodes.Forbidden, AppErrorKind.Forbidden);
    }

    [Fact]
    public async Task OwnerStillGetsNotFoundForMissingStaff()
    {
        await AssertError(
            () => access.RequireStaffManagementAsync(new Actor(owner.Id), Guid.Empty, default),
            ErrorCodes.StaffNotFound, AppErrorKind.NotFound);
    }

    [Fact]
    public async Task StaleCallerIsRejectedBeforeCheckingStaff()
    {
        await AssertError(
            () => access.RequireStaffManagementAsync(new Actor(Guid.NewGuid()), Guid.Empty, default),
            ErrorCodes.Unauthorized, AppErrorKind.Unauthorized);
    }

    private static User CreateUser(long telegramId) =>
        new(telegramId, $"User {telegramId}", null, DateTime.UnixEpoch);

    private static async Task AssertError(Func<Task> action, string code, AppErrorKind kind)
    {
        var error = await Assert.ThrowsAsync<AppException>(action);
        Assert.Equal(code, error.Code);
        Assert.Equal(kind, error.Kind);
    }

    private sealed class TestStore(User[] users, StaffMember[] staff) : IUserRepository, IStaffRepository
    {
        Task<User?> IUserRepository.GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(users.SingleOrDefault(user => user.Id == id));

        Task<StaffMember?> IStaffRepository.GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(staff.SingleOrDefault(profile => profile.Id == id));

        public Task<User?> GetByTelegramUserIdAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<User> InsertOrGetAsync(User user, CancellationToken ct) => throw new NotSupportedException();
        public void Add(User user) => throw new NotSupportedException();
        public Task<StaffMember?> GetByUserIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<StaffMember>> ListAsync(bool includeInactive, CancellationToken ct) => throw new NotSupportedException();
        public Task<StaffMember?> GetByIdForUpdateAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<StaffMember?> GetByIdSnapshotAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateWithCoordinationAsync(StaffMember profile, CancellationToken ct) => throw new NotSupportedException();
        public void Add(StaffMember profile) => throw new NotSupportedException();
    }
}
