using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

public sealed class BookingCoordinationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerChangeRejectsBookingWithEarlierVersion(bool deactivateStaff)
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var ownerDb = fixture.CreateContext();
        await using var bookingDb = fixture.CreateContext();
        var clock = new FixedClock();

        await new DatabaseSeeder(ownerDb).SeedBusinessSettingsAsync(
            new BusinessSettings("Studio", "Staff", "UTC", "AMD"), ct);
        var user = new User(8000000010, "Owner", null, clock.UtcNow);
        ownerDb.Users.Add(user);
        await ownerDb.SaveChangesAsync(ct);
        var actor = new Actor(user.Id);
        var access = new OwnerAccess();
        var ownerCoordination = new BookingCoordinationRepository(ownerDb);
        var ownerLock = new ObservedLock(new PostgresStaffOperationLock(ownerDb));
        var staffService = new StaffManagementService(access, new UserRepository(ownerDb),
            new StaffRepository(ownerDb), ownerCoordination, ownerLock, ownerDb);
        var staff = await staffService.CreateAsync(actor, user.Id, "Staff", ct);
        var service = new Service("Consultation");
        var offering = new StaffService(staff.Id, service.Id, 25m, 30);
        ownerDb.AddRange(service, offering, new WorkingHours(staff.Id, new(9, 0), new(17, 0)));
        await ownerDb.SaveChangesAsync(ct);

        var catalog = new CatalogRepository(bookingDb);
        var bookings = new BookingRepository(bookingDb, clock);
        var availability = new AvailabilityService(catalog, new StaffRepository(bookingDb),
            new ScheduleRepository(bookingDb), new BusinessSettingsRepository(bookingDb), bookings,
            clock, new AvailabilityCalculator(new BookingOptions()));

        await using var provider = new ServiceCollection().BuildServiceProvider();
        var bookingLock = new PausingLock(new PostgresStaffOperationLock(bookingDb));
        var bookingService = new BookingService(bookings, provider.GetRequiredService<IServiceScopeFactory>(),
            access, catalog, bookingDb, bookingLock, availability, clock, new BookingCoordinationRepository(bookingDb),
            new BusinessSettingsRepository(bookingDb));
        var request = new CreateBookingRequest(Guid.NewGuid(), user.Id, offering.Id,
            clock.UtcNow.AddHours(2), 25m, 30);

        var booking = bookingService.CreateAsync(actor, request, ct);
        await bookingLock.Acquired.Task.WaitAsync(ct);

        Task? deactivation = null;
        try
        {
            if (deactivateStaff)
            {
                deactivation = staffService.SetActiveAsync(actor, staff.Id, false, ct);
                // The service has committed the increment by the time it requests this lock.
                await ownerLock.Requested.Task.WaitAsync(ct);
            }
            else
            {
                var settings = new BusinessSettingsService(access, new BusinessSettingsRepository(ownerDb),
                    new BookingRepository(ownerDb, clock), ownerCoordination, ownerDb);
                await settings.UpdateTimeZoneAndCurrencyAsync(actor, "UTC", "USD", ct);
            }
        }
        finally
        {
            bookingLock.Resume.TrySetResult();
        }

        var error = await Assert.ThrowsAsync<AppException>(() => booking);
        Assert.Equal(ErrorCodes.ConfigurationChanged, error.Code);
        if (deactivation is not null)
        {
            await deactivation;
        }

        Assert.False(await bookingDb.Bookings.AnyAsync(ct));
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    }

    private sealed class PausingLock(IStaffOperationLock inner) : IStaffOperationLock
    {
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
        {
            await inner.AcquireAsync(staffMemberId, ct);
            Acquired.TrySetResult();
            await Resume.Task.WaitAsync(ct);
        }
    }

    private sealed class ObservedLock(IStaffOperationLock inner) : IStaffOperationLock
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
        {
            Requested.TrySetResult();
            return inner.AcquireAsync(staffMemberId, ct);
        }
    }

    private sealed class OwnerAccess : IAccessService
    {
        public Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) => Task.FromResult(true);
        public Task RequireOwnerAsync(Actor actor, CancellationToken ct) => Task.CompletedTask;
        public Task RequireStaffManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RequireAppointmentManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
