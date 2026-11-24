using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingTermsServiceTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerOrLinkedEmployeeCanCreateOfferingWithIndependentTerms(bool owner)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var serviceId = await AddService();
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var result = await Service(scope.ServiceProvider).CreateAsync(
            new Actor(owner ? setup.Owner.Id : setup.Employee.Id), setup.Staff.Id, serviceId, new(0m, 15), default);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(setup.Staff.Id, result.StaffMemberId);
        Assert.Equal(serviceId, result.ServiceId);
        Assert.Equal(0m, result.Price);
        Assert.Equal(15, result.DurationMinutes);
        Assert.True(result.IsActive);
        await using var verify = fixture.CreateContext();
        var saved = await verify.StaffServices.SingleAsync(o => o.Id == result.Id);
        Assert.Equal(0m, saved.Price);
        Assert.Equal(15, saved.DurationMinutes);
        Assert.Equal(4, await verify.StaffServices.CountAsync(o => o.StaffMemberId == setup.Staff.Id));
    }

    [Fact]
    public async Task SameServiceCanHaveDifferentTermsForTwoEmployees()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var created = await Service(scope.ServiceProvider).CreateAsync(new Actor(setup.Owner.Id),
            setup.Other.Id, setup.ActiveService.Id, new(75m, 90), default);
        Assert.NotEqual(setup.Active.Id, created.Id);
        Assert.Equal(75m, created.Price);
        Assert.Equal(90, created.DurationMinutes);
        await using var verify = fixture.CreateContext();
        var original = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(12.50m, original.Price);
        Assert.Equal(30, original.DurationMinutes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateStaffServicePairIsRejectedWithoutChangingOriginal(bool disabled)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var offering = disabled ? setup.Disabled : setup.Active;
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).CreateAsync(
            new Actor(setup.Employee.Id), setup.Staff.Id, offering.ServiceId, new(99m, 99), default));
        Assert.Equal(OfferingTermsService.DuplicateOfferingCode, error.Code);
        Assert.Equal(AppErrorKind.Conflict, error.Kind);
        await using var db = fixture.CreateContext();
        var saved = Assert.Single(await db.StaffServices.Where(o => o.StaffMemberId == setup.Staff.Id &&
            o.ServiceId == offering.ServiceId).ToListAsync());
        Assert.Equal(offering.Id, saved.Id);
        Assert.Equal(offering.Price, saved.Price);
        Assert.Equal(offering.DurationMinutes, saved.DurationMinutes);
        Assert.Equal(offering.IsActive, saved.IsActive);
    }

    [Theory]
    [InlineData("-0.01", 30)]
    [InlineData("1.001", 30)]
    [InlineData("10000000000", 30)]
    [InlineData("5.25", 0)]
    [InlineData("5.25", -1)]
    public async Task InvalidTermsRejectBothCreateAndUpdateWithoutPersisting(string price, int duration)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var serviceId = await AddService();
        var request = new OfferingTermsRequest(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), duration);
        foreach (var create in new[] { true, false })
        {
            await using var scope = setup.Host.Services.CreateAsyncScope();
            var service = Service(scope.ServiceProvider);
            var error = await Assert.ThrowsAsync<AppException>(() => create
                ? service.CreateAsync(new Actor(setup.Employee.Id), setup.Staff.Id, serviceId, request, default)
                : service.UpdateAsync(new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, request, default));
            Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
            Assert.Equal(AppErrorKind.Validation, error.Kind);
        }
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == serviceId));
        var original = await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(12.50m, original.Price);
        Assert.Equal(30, original.DurationMinutes);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("5.2500", 45)]
    [InlineData("9999999999.99", 60)]
    public async Task AllowedBoundariesArePersistedWithoutRounding(string price, int duration)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var expected = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var result = await Service(scope.ServiceProvider).UpdateAsync(new Actor(setup.Employee.Id),
            setup.Staff.Id, setup.ActiveService.Id, new(expected, duration), default);
        Assert.Equal(setup.Active.Id, result.Id);
        Assert.Equal(expected, result.Price);
        Assert.Equal(duration, result.DurationMinutes);
        await using var db = fixture.CreateContext();
        var saved = await db.StaffServices.SingleAsync(o => o.Id == result.Id);
        Assert.Equal(expected, saved.Price);
        Assert.Equal(duration, saved.DurationMinutes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomerAndUnrelatedEmployeeCannotWrite(bool update)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var serviceId = await AddService();
        foreach (var (actor, staffId) in new[]
                 { (new Actor(setup.Customer.Id), setup.Staff.Id), (new Actor(setup.Employee.Id), setup.Other.Id) })
        {
            await using var scope = setup.Host.Services.CreateAsyncScope();
            var service = Service(scope.ServiceProvider);
            var error = await Assert.ThrowsAsync<AppException>(() => update
                ? service.UpdateAsync(actor, staffId, setup.ActiveService.Id, new(42m, 60), default)
                : service.CreateAsync(actor, staffId, serviceId, new(42m, 60), default));
            Assert.Equal(ErrorCodes.Forbidden, error.Code);
        }
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.ServiceId == serviceId));
        Assert.Equal(12.50m, (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Fact]
    public async Task TermsEditPreservesActivationAndEveryAcceptedBookingSnapshot()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var now = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = fixture.CreateContext())
        {
            foreach (var status in Enum.GetValues<BookingStatus>())
            {
                var booking = new Booking(setup.Customer.Id, setup.Staff.Id, setup.Disabled.Id, Guid.NewGuid(),
                    now.AddDays(-1), 25m, 45, now.AddDays(-2));
                if (status == BookingStatus.Cancelled) booking.Cancel(now.AddDays(-1).AddHours(-1));
                if (status == BookingStatus.Completed) booking.Complete(now);
                db.Bookings.Add(booking);
            }
            await db.SaveChangesAsync();
        }
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var result = await Service(scope.ServiceProvider).UpdateAsync(new Actor(setup.Employee.Id),
            setup.Staff.Id, setup.Disabled.ServiceId, new(80m, 90), default);
        Assert.False(result.IsActive);
        Assert.Equal(setup.Disabled.Id, result.Id);
        await using var verify = fixture.CreateContext();
        var bookings = await verify.Bookings.Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync();
        Assert.Equal(3, bookings.Count);
        Assert.All(bookings, b =>
        {
            Assert.Equal(25m, b.Price);
            Assert.Equal(45, b.DurationMinutes);
            Assert.Equal(now.AddDays(-1), b.StartsAtUtc);
            Assert.Equal(now.AddDays(-1).AddMinutes(45), b.EndsAtUtc);
            Assert.Equal(setup.Disabled.Id, b.StaffServiceId);
        });
        Assert.Equal(3, bookings.Select(b => b.Status).Distinct().Count());
    }

    [Fact]
    public async Task MissingResourcesUseSharedErrorsAndUpdateDoesNotCreate()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var serviceId = await AddService();
        foreach (var (staffId, sharedId, create, code) in new[]
                 {
                     (Guid.NewGuid(), serviceId, true, ErrorCodes.StaffNotFound),
                     (Guid.Empty, serviceId, false, ErrorCodes.StaffNotFound),
                     (setup.Staff.Id, Guid.NewGuid(), true, ErrorCodes.ServiceNotFound),
                     (setup.Staff.Id, Guid.Empty, false, ErrorCodes.ServiceNotFound),
                     (setup.Staff.Id, serviceId, false, ErrorCodes.StaffServiceNotFound)
                 })
        {
            await using var scope = setup.Host.Services.CreateAsyncScope();
            var service = Service(scope.ServiceProvider);
            var error = await Assert.ThrowsAsync<AppException>(() => create
                ? service.CreateAsync(new Actor(setup.Owner.Id), staffId, sharedId, new(40m, 45), default)
                : service.UpdateAsync(new Actor(setup.Owner.Id), staffId, sharedId, new(40m, 45), default));
            Assert.Equal(code, error.Code);
            Assert.Equal(AppErrorKind.NotFound, error.Kind);
        }
        await using var db = fixture.CreateContext();
        Assert.False(await db.StaffServices.AnyAsync(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == serviceId));
    }

    [Fact]
    public async Task PreloadedStaffCannotBypassFreshDeactivationCheck()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        var tracked = await scope.ServiceProvider.GetRequiredService<IStaffRepository>().GetByIdAsync(setup.Staff.Id);
        Assert.True(tracked!.IsActive);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).UpdateAsync(
            new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, new(90m, 90), default));
        Assert.Equal(ErrorCodes.StaffInactive, error.Code);
        await using var verify = fixture.CreateContext();
        Assert.Equal(12.50m, (await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    [Fact]
    public async Task ReloadedOfferingPreservesConcurrentStatusChangeAndOwnerCanEditInactiveStaff()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await using var scope = setup.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICatalogRepository>()
            .GetOfferingAsync(setup.Staff.Id, setup.ActiveService.Id, default);
        await using (var db = fixture.CreateContext())
        {
            (await db.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).Deactivate();
            (await db.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var result = await Service(scope.ServiceProvider).UpdateAsync(new Actor(setup.Owner.Id),
            setup.Staff.Id, setup.ActiveService.Id, new(35m, 50), default);
        Assert.False(result.IsActive);
        await using var verify = fixture.CreateContext();
        var saved = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.False(saved.IsActive);
        Assert.Equal(35m, saved.Price);
        Assert.Equal(50, saved.DurationMinutes);
        Assert.False((await verify.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).IsActive);
    }

    [Fact]
    public async Task CancellationBeforeWriteLeavesTermsUnchanged()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var scope = setup.Host.Services.CreateAsyncScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(scope.ServiceProvider).UpdateAsync(
            new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, new(90m, 90), cancellation.Token));
        await using var verify = fixture.CreateContext();
        Assert.Equal(12.50m, (await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id)).Price);
    }

    private static OfferingTermsService Service(IServiceProvider provider) => new(
        provider.GetRequiredService<ICatalogRepository>(), provider.GetRequiredService<IAccessService>(),
        provider.GetRequiredService<IStaffRepository>(), provider.GetRequiredService<IUnitOfWork>(),
        provider.GetRequiredService<IStaffOperationLock>());

    private async Task<Guid> AddService()
    {
        await using var db = fixture.CreateContext();
        var service = new Service("New terms service " + Guid.NewGuid());
        db.Services.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }
}
