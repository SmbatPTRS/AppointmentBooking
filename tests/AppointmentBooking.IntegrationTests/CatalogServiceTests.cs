using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogServiceTests(PostgresFixture fixture)
{
    private sealed class OwnerAccess(bool denied = false) : IAccessService
    {
        public Task RequireOwnerAsync(Actor actor, CancellationToken ct) => denied
            ? throw new AppException(ErrorCodes.Forbidden, "Owner required.", AppErrorKind.Forbidden)
            : Task.CompletedTask;
        public Task RequireStaffManagementAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task RequireAppointmentManagementAsync(Actor actor, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(200)]
    public async Task CreatePersistsTrimmedActiveService(int length)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(), db);
        var name = new string('a', length);
        var result = await sut.CreateServiceAsync(new Actor(Guid.NewGuid()), new CreateServiceRequest("  " + name + "  "), default);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(name, result.Name);
        Assert.True(result.IsActive);
        db.ChangeTracker.Clear();
        var saved = await db.Services.SingleAsync(s => s.Id == result.Id);
        Assert.Equal(name, saved.Name);
        Assert.True(saved.IsActive);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 201)]
    [InlineData(true, 7)]
    public async Task RejectedCreateDoesNotStageOrPersistService(bool denied, int length)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var before = await db.Services.CountAsync();
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(denied), db);
        var error = await Assert.ThrowsAsync<AppException>(() => sut.CreateServiceAsync(
            new Actor(Guid.NewGuid()), new CreateServiceRequest("  " + new string('a', length) + "  "), default));
        Assert.Equal(denied ? ErrorCodes.Forbidden : ErrorCodes.ValidationFailed, error.Code);
        Assert.Equal(denied ? AppErrorKind.Forbidden : AppErrorKind.Validation, error.Kind);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(before, await db.Services.CountAsync());
    }
    [Fact]
    public async Task CustomerServiceListExcludesInactiveServices()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var active = new Service("Visible service");
        var hidden = new Service("Hidden service");
        hidden.Deactivate();
        db.AddRange(active, hidden);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Customer reads must not require owner privileges.
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(true), db);
        var result = await sut.ListActiveServicesAsync(default);
        Assert.Contains(new ServiceDto(active.Id, active.Name, true), result);
        Assert.DoesNotContain(result, s => s.Id == hidden.Id);
        Assert.All(result, s => Assert.True(s.IsActive));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task CustomerOfferingsRequireActiveStaffServiceAndOffering(bool staffActive, bool serviceActive, bool offeringActive)
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Catalogue", null, DateTime.UtcNow);
        var otherUser = new User(Random.Shared.NextInt64(5000000000, 9000000000), "Other", null, DateTime.UtcNow);
        var staff = new StaffMember(user.Id, "Catalogue");
        var other = new StaffMember(otherUser.Id, "Other");
        var service = new Service("Haircut");
        var offering = new StaffService(staff.Id, service.Id, 5000.25m, 40);
        var otherOffering = new StaffService(other.Id, service.Id, 7000m, 60);
        if (!staffActive) staff.Deactivate();
        if (!serviceActive) service.Deactivate();
        if (!offeringActive) offering.Deactivate();
        db.AddRange(user, otherUser, staff, other, service, offering, otherOffering);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var sut = new CatalogService(new CatalogRepository(db), new OwnerAccess(true), db);
        var result = await sut.ListActiveOfferingsAsync(staff.Id, default);
        if (staffActive && serviceActive && offeringActive)
            Assert.Equal(new StaffServiceDto(offering.Id, staff.Id, service.Id, "Haircut", 5000.25m, 40, true), Assert.Single(result));
        else Assert.Empty(result);
        Assert.Empty(await sut.ListActiveOfferingsAsync(Guid.NewGuid(), default));
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
