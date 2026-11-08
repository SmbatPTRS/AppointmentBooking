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
}
