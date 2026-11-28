using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingActivationServiceTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationPublishesOnlyAffectedStaffVersionAndReactivationPreservesTerms(bool owner)
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        var otherBefore = await Versions(setup.Other.Id);
        var actor = new Actor(owner ? setup.Owner.Id : setup.Employee.Id);
        await using (var scope = setup.Host.Services.CreateAsyncScope())
        {
            var result = await Service(scope.ServiceProvider).SetActiveAsync(actor, setup.Staff.Id, setup.ActiveService.Id, false, default);
            Assert.False(result.IsActive);
            Assert.Equal(setup.Active.Id, result.Id);
            Assert.Equal(12.50m, result.Price);
            Assert.Equal(30, result.DurationMinutes);
        }
        var disabled = await Versions(setup.Staff.Id);
        Assert.Equal(before.BusinessVersion, disabled.BusinessVersion);
        Assert.Equal(before.StaffVersion + 1, disabled.StaffVersion);
        Assert.Equal(otherBefore, await Versions(setup.Other.Id));
        await using (var scope = setup.Host.Services.CreateAsyncScope())
        {
            var result = await Service(scope.ServiceProvider).SetActiveAsync(actor, setup.Staff.Id, setup.ActiveService.Id, true, default);
            Assert.True(result.IsActive);
            Assert.Equal(setup.Active.Id, result.Id);
        }
        Assert.Equal(disabled, await Versions(setup.Staff.Id));
        await using var verify = fixture.CreateContext();
        var saved = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.True(saved.IsActive);
        Assert.Equal(12.50m, saved.Price);
        Assert.Equal(30, saved.DurationMinutes);
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id)).IsActive);
    }

    [Fact]
    public async Task RepeatedDeactivationDoesNotAdvanceVersionOrCreateAnotherOffering()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = setup.Host.Services.CreateAsyncScope();
            var result = await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Employee.Id),
                setup.Staff.Id, setup.Disabled.ServiceId, false, default);
            Assert.Equal(setup.Disabled.Id, result.Id);
            Assert.False(result.IsActive);
        }
        Assert.Equal(before, await Versions(setup.Staff.Id));
        await using var verify = fixture.CreateContext();
        Assert.Equal(3, await verify.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
    }

    [Fact]
    public async Task UnauthorizedCallersCannotChangeActivationOrPublishVersions()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        var otherBefore = await Versions(setup.Other.Id);
        foreach (var (actor, target, expected) in new[]
                 {
                     (new Actor(setup.Customer.Id), setup.Staff.Id, ErrorCodes.Forbidden),
                     (new Actor(setup.Employee.Id), setup.Other.Id, ErrorCodes.Forbidden),
                     (new Actor(Guid.NewGuid()), setup.Staff.Id, ErrorCodes.Unauthorized)
                 })
        {
            await using var scope = setup.Host.Services.CreateAsyncScope();
            var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).SetActiveAsync(
                actor, target, setup.ActiveService.Id, false, default));
            Assert.Equal(expected, error.Code);
        }
        Assert.Equal(before, await Versions(setup.Staff.Id));
        Assert.Equal(otherBefore, await Versions(setup.Other.Id));
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).IsActive);
    }

    [Fact]
    public async Task MissingStaffServiceOrOfferingUsesSharedNotFoundErrors()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        foreach (var (staffId, serviceId, code) in new[]
                 {
                     (Guid.Empty, setup.ActiveService.Id, ErrorCodes.StaffNotFound),
                     (Guid.NewGuid(), setup.ActiveService.Id, ErrorCodes.StaffNotFound),
                     (setup.Staff.Id, Guid.NewGuid(), ErrorCodes.ServiceNotFound),
                     (setup.Other.Id, setup.ActiveService.Id, ErrorCodes.StaffServiceNotFound)
                 })
        {
            await using var scope = setup.Host.Services.CreateAsyncScope();
            var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).SetActiveAsync(
                new Actor(setup.Owner.Id), staffId, serviceId, false, default));
            Assert.Equal(code, error.Code);
            Assert.Equal(AppErrorKind.NotFound, error.Kind);
        }
        Assert.Equal(before, await Versions(setup.Staff.Id));
    }

    [Fact]
    public async Task InactiveSharedServiceAllowsDeactivationButRejectsReactivation()
    {
        await using var setup = await Seed();
        await using (var scope = setup.Host.Services.CreateAsyncScope())
            await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Employee.Id),
                setup.Staff.Id, setup.InactiveService.Id, false, default);
        var before = await Versions(setup.Staff.Id);
        await using (var scope = setup.Host.Services.CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).SetActiveAsync(
                new Actor(setup.Employee.Id), setup.Staff.Id, setup.InactiveService.Id, true, default));
            Assert.Equal(ErrorCodes.ServiceInactive, error.Code);
            Assert.Equal(AppErrorKind.Conflict, error.Kind);
        }
        Assert.Equal(before, await Versions(setup.Staff.Id));
        await using var verify = fixture.CreateContext();
        Assert.False((await verify.StaffServices.SingleAsync(o => o.Id == setup.Hidden.Id)).IsActive);
        Assert.False((await verify.Services.SingleAsync(s => s.Id == setup.InactiveService.Id)).IsActive);
    }

    [Fact]
    public async Task FreshStatePreventsCachedActiveStaffFromDeactivatingAnOffering()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStaffRepository>().GetByIdAsync(setup.Staff.Id);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).SetActiveAsync(
            new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, false, default));
        Assert.Equal(ErrorCodes.StaffInactive, error.Code);
        Assert.Equal(before, await Versions(setup.Staff.Id));
    }

    [Fact]
    public async Task CachedOfferingDoesNotCauseAnExtraVersionIncrementAfterDeactivation()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICatalogRepository>().GetOfferingAsync(setup.Staff.Id, setup.ActiveService.Id, default);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var result = await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Employee.Id),
            setup.Staff.Id, setup.ActiveService.Id, false, default);
        Assert.False(result.IsActive);
        Assert.Equal(before, await Versions(setup.Staff.Id));
    }

    [Fact]
    public async Task OwnerMayPrepareInactiveStaffOfferingWithoutActivatingTheStaffProfile()
    {
        await using var setup = await Seed();
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var before = await Versions(setup.Staff.Id);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var result = await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Owner.Id),
            setup.Staff.Id, setup.Disabled.ServiceId, true, default);
        Assert.True(result.IsActive);
        Assert.Equal(before, await Versions(setup.Staff.Id));
        await using var verify = fixture.CreateContext();
        Assert.False((await verify.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).IsActive);
    }

    [Fact]
    public async Task DeactivationPreservesConfirmedCancelledAndCompletedBookingSnapshots()
    {
        await using var setup = await Seed();
        var now = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = fixture.CreateContext())
        {
            foreach (var status in Enum.GetValues<BookingStatus>())
            {
                var booking = new Booking(setup.Customer.Id, setup.Staff.Id, setup.Active.Id, Guid.NewGuid(),
                    now.AddDays(1), 12.50m, 30, now);
                if (status == BookingStatus.Cancelled) booking.Cancel(now);
                if (status == BookingStatus.Completed)
                {
                    // Domain completion is arranged at the accepted end for this fixture.
                    booking.Complete(now.AddDays(1).AddMinutes(30));
                }
                db.Bookings.Add(booking);
            }
            await db.SaveChangesAsync();
        }
        await using var scope = setup.Host.Services.CreateAsyncScope();
        await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, false, default);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Bookings.Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync();
        Assert.Equal(3, saved.Count);
        Assert.Equal(3, saved.Select(b => b.Status).Distinct().Count());
        Assert.All(saved, b =>
        {
            Assert.Equal(setup.Active.Id, b.StaffServiceId);
            Assert.Equal(12.50m, b.Price);
            Assert.Equal(30, b.DurationMinutes);
            Assert.Equal(now.AddDays(1), b.StartsAtUtc);
            Assert.Equal(now.AddDays(1).AddMinutes(30), b.EndsAtUtc);
        });
    }

    [Fact]
    public async Task FailedMutationRollsBackStatusButKeepsThePublishedInvalidationLikeStaffDeactivation()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Staff.Id);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Assert.ThrowsAsync<IOException>(() => Service(scope.ServiceProvider, new FailingSave(db)).SetActiveAsync(
            new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, false, default));
        var after = await Versions(setup.Staff.Id);
        Assert.Equal(before.BusinessVersion, after.BusinessVersion);
        Assert.Equal(before.StaffVersion + 1, after.StaffVersion);
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).IsActive);
    }

    private static OfferingActivationService Service(IServiceProvider provider, IUnitOfWork? unitOfWork = null) => new(
        provider.GetRequiredService<ICatalogRepository>(), provider.GetRequiredService<IAccessService>(),
        provider.GetRequiredService<IStaffRepository>(), provider.GetRequiredService<IBookingCoordinationRepository>(),
        unitOfWork ?? provider.GetRequiredService<IUnitOfWork>(), provider.GetRequiredService<IStaffOperationLock>());

    private async Task<OfferingOverviewScenario> Seed()
    {
        var setup = await OfferingOverviewScenario.Create(fixture);
        await using var db = fixture.CreateContext();
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null) db.Add(settings = new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == settings.Id)) db.Add(new BookingCoordination(settings));
        db.AddRange(new BookingCoordination(setup.Staff), new BookingCoordination(setup.Other));
        await db.SaveChangesAsync();
        return setup;
    }

    private async Task<BookingCoordinationVersions> Versions(Guid staffId)
    {
        await using var db = fixture.CreateContext();
        return await new BookingCoordinationRepository(db).ReadVersionsAsync(staffId, default);
    }

    private sealed class FailingSave(AppDbContext db) : IUnitOfWork
    {
        public Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct) => db.BeginTransactionAsync(ct);
        public Task<int> SaveChangesAsync(CancellationToken ct) => throw new IOException("Injected save failure");
    }
}
