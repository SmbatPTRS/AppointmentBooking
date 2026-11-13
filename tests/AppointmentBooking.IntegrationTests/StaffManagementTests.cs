using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class StaffManagementTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreatePersistsOneProfileAndOneVersionRow()
    {
        await using var db = fixture.CreateContext();
        var user = new User(8_000_000_002, "Staff user", null, DateTime.UtcNow);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var actor = new Actor(user.Id);
        var service = new StaffManagementService(new OwnerAccess(actor), new UserRepository(db),
            new StaffRepository(db), new BookingCoordinationRepository(db),
            new PostgresStaffOperationLock(db), db);

        var profile = await service.CreateAsync(actor, user.Id, "  Staff name  ");
        var duplicate = await Assert.ThrowsAsync<AppException>(
            () => service.CreateAsync(actor, user.Id, "Duplicate"));

        Assert.Equal("Staff name", profile.DisplayName);
        Assert.True(profile.IsActive);
        Assert.Equal(ErrorCodes.StaffAlreadyExists, duplicate.Code);
        Assert.Equal(1, await db.StaffMembers.CountAsync(staff => staff.UserId == user.Id));
        var coordination = await db.BookingCoordinationRows.SingleAsync(row => row.StaffMemberId == profile.Id);
        Assert.Null(coordination.BusinessSettingsId);
        Assert.Equal(0L, coordination.ChangeVersion);

        var inactive = await service.SetActiveAsync(actor, profile.Id, false);
        Assert.False(inactive.IsActive);
        Assert.Equal(1L, await db.BookingCoordinationRows
            .Where(row => row.StaffMemberId == profile.Id).Select(row => row.ChangeVersion).SingleAsync());
    }

    [Fact]
    public async Task VersionIncrementSurvivesFailedDeactivation()
    {
        await using var db = fixture.CreateContext();
        var user = new User(8_000_000_003, "Staff user", null, DateTime.UtcNow);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var actor = new Actor(user.Id);
        var service = new StaffManagementService(new OwnerAccess(actor), new UserRepository(db),
            new StaffRepository(db), new BookingCoordinationRepository(db), new FailingStaffLock(), db);
        var profile = await service.CreateAsync(actor, user.Id, "Staff name");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetActiveAsync(actor, profile.Id, false));

        await using var readDb = fixture.CreateContext();
        Assert.True((await readDb.StaffMembers.SingleAsync(staff => staff.Id == profile.Id)).IsActive);
        Assert.Equal(1L, (await readDb.BookingCoordinationRows
            .SingleAsync(row => row.StaffMemberId == profile.Id)).ChangeVersion);
    }

    [Fact]
    public async Task AppointmentStaffBrowseIncludesAllInactiveProfilesForOwner()
    {
        await using var db = fixture.CreateContext();
        var ownerUser = new User(8_000_000_004, "Owner", null, DateTime.UtcNow);
        var employeeUser = new User(8_000_000_005, "Staff user", null, DateTime.UtcNow);
        db.Users.AddRange(ownerUser, employeeUser);
        await db.SaveChangesAsync();

        var owner = new Actor(ownerUser.Id);
        var access = new OwnerAccess(owner);
        var service = new StaffManagementService(access, new UserRepository(db),
            new StaffRepository(db), new BookingCoordinationRepository(db),
            new PostgresStaffOperationLock(db), db);
        var profile = await service.CreateAsync(owner, employeeUser.Id, "Inactive staff");
        await service.SetActiveAsync(owner, profile.Id, false);

        var listed = await service.BrowseForAppointmentsAsync(owner);

        Assert.Contains(new StaffListItem(profile.Id, "Inactive staff", false), listed);
    }

    [Fact]
    public async Task InactiveEmployeeBrowseIncludesOnlyTheirOwnInactiveProfile()
    {
        await using var db = fixture.CreateContext();
        var ownerUser = new User(8_000_000_006, "Owner", null, DateTime.UtcNow);
        var employeeUser = new User(8_000_000_007, "Staff user", null, DateTime.UtcNow);
        db.Users.AddRange(ownerUser, employeeUser);
        await db.SaveChangesAsync();

        var owner = new Actor(ownerUser.Id);
        var employee = new Actor(employeeUser.Id);
        var access = new OwnerAccess(owner);
        var service = new StaffManagementService(access, new UserRepository(db),
            new StaffRepository(db), new BookingCoordinationRepository(db),
            new PostgresStaffOperationLock(db), db);
        var own = await service.CreateAsync(owner, employeeUser.Id, "Inactive staff");
        await service.SetActiveAsync(owner, own.Id, false);

        var listed = await service.BrowseForAppointmentsAsync(employee);

        Assert.Equal([new StaffListItem(own.Id, "Inactive staff", false)], listed);
    }

    private sealed class FailingStaffLock : IStaffOperationLock
    {
        public Task AcquireAsync(Guid staffMemberId, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated lock failure.");
    }

    private sealed class OwnerAccess(Actor owner) : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) =>
            Task.FromResult(actor == owner);

        public Task RequireOwnerAsync(Actor actor, CancellationToken ct)
        {
            Assert.Equal(owner, actor);
            return Task.CompletedTask;
        }

        public Task RequireStaffManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
            RequireOwnerAsync(actor, ct);

        public Task RequireAppointmentManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
