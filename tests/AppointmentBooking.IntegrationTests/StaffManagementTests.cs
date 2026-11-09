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

    private sealed class FailingStaffLock : IStaffOperationLock
    {
        public Task AcquireAsync(Guid staffMemberId, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated lock failure.");
    }

    private sealed class OwnerAccess(Actor owner) : IAccessService
    {
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
