using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OfferingTermsConcurrencyTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(true, "price")]
    [InlineData(false, "price")]
    [InlineData(true, "duration")]
    [InlineData(false, "duration")]
    [InlineData(true, "both")]
    [InlineData(false, "both")]
    public async Task BookingAndTermsEditUseOneCompleteSnapshotInBothLockOrders(bool bookingFirst, string change)
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var start = await Prepare(setup);
        var gate = new LockGate();
        await using var app = setup.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Configure(services, gate)));
        await using var bookingScope = app.Services.CreateAsyncScope();
        await using var editScope = app.Services.CreateAsyncScope();
        var bookingService = bookingScope.ServiceProvider.GetRequiredService<BookingService>();
        var terms = editScope.ServiceProvider.GetRequiredService<OfferingTermsService>();
        var request = new CreateBookingRequest(Guid.NewGuid(), setup.Customer.Id, setup.Active.Id, start, 12.50m, 30);
        var updated = new OfferingTermsRequest(change == "duration" ? 12.50m : 37.75m, change == "price" ? 30 : 60);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<BookingDto>? booking = null;
        Task<StaffServiceDto>? edit = null;
        Task Book() => booking = bookingService.CreateAsync(new Actor(setup.Customer.Id), request, timeout.Token);
        Task Edit() => edit = terms.UpdateAsync(new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, updated, timeout.Token);
        var errors = await RunBlockedRace(bookingFirst ? Book : Edit, bookingFirst ? Edit : Book, gate, timeout);
        Assert.Null(errors[0]);
        Assert.Equal(updated.Price, (await edit!).Price);
        Assert.Equal(updated.DurationMinutes, (await edit!).DurationMinutes);
        await using var verify = fixture.CreateContext();
        var storedOffering = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(updated.Price, storedOffering.Price);
        Assert.Equal(updated.DurationMinutes, storedOffering.DurationMinutes);
        var storedBookings = await verify.Bookings.Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync();
        if (bookingFirst)
        {
            Assert.Null(errors[1]);
            var saved = Assert.Single(storedBookings);
            Assert.Equal((await booking!).Id, saved.Id);
            Assert.Equal(12.50m, saved.Price);
            Assert.Equal(30, saved.DurationMinutes);
            Assert.Equal(start.AddMinutes(30), saved.EndsAtUtc);
        }
        else
        {
            var rejected = Assert.IsType<AppException>(errors[1]);
            Assert.Equal(ErrorCodes.OfferingChanged, rejected.Code);
            Assert.Equal(AppErrorKind.Conflict, rejected.Kind);
            Assert.Empty(storedBookings);
            // A new confirmation with current terms succeeds after the stale one was rejected.
            await using var fresh = app.Services.CreateAsyncScope();
            var accepted = await fresh.ServiceProvider.GetRequiredService<BookingService>().CreateAsync(
                new Actor(setup.Customer.Id), request with
                {
                    RequestId = Guid.NewGuid(), ExpectedPrice = updated.Price,
                    ExpectedDurationMinutes = updated.DurationMinutes
                }, timeout.Token);
            Assert.Equal(updated.Price, accepted.Price);
            Assert.Equal(updated.DurationMinutes, accepted.DurationMinutes);
            var saved = await verify.Bookings.AsNoTracking().SingleAsync(b => b.StaffMemberId == setup.Staff.Id);
            Assert.Equal(accepted.Id, saved.Id);
            Assert.Equal(updated.Price, saved.Price);
            Assert.Equal(updated.DurationMinutes, saved.DurationMinutes);
            Assert.Equal(start.AddMinutes(updated.DurationMinutes), saved.EndsAtUtc);
        }
    }

    [Fact]
    public async Task ConcurrentCreationOfTheSamePairPersistsOneOfferingAndRejectsTheWaitingWrite()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        Guid serviceId;
        await using (var db = fixture.CreateContext())
        {
            var service = new Service("Concurrent terms " + Guid.NewGuid());
            db.Services.Add(service);
            await db.SaveChangesAsync();
            serviceId = service.Id;
        }
        var gate = new LockGate();
        await using var app = setup.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Configure(services, gate)));
        await using var firstScope = app.Services.CreateAsyncScope();
        await using var secondScope = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<StaffServiceDto>? created = null;
        var errors = await RunBlockedRace(
            () => created = firstScope.ServiceProvider.GetRequiredService<OfferingTermsService>().CreateAsync(
                new Actor(setup.Owner.Id), setup.Staff.Id, serviceId, new(15m, 20), timeout.Token),
            () => secondScope.ServiceProvider.GetRequiredService<OfferingTermsService>().CreateAsync(
                new Actor(setup.Employee.Id), setup.Staff.Id, serviceId, new(99m, 90), timeout.Token), gate, timeout);
        Assert.Null(errors[0]);
        var conflict = Assert.IsType<AppException>(errors[1]);
        Assert.Equal(OfferingTermsService.DuplicateOfferingCode, conflict.Code);
        Assert.Equal(AppErrorKind.Conflict, conflict.Kind);
        await using var verify = fixture.CreateContext();
        var saved = Assert.Single(await verify.StaffServices.Where(o => o.StaffMemberId == setup.Staff.Id && o.ServiceId == serviceId).ToListAsync());
        Assert.Equal((await created!).Id, saved.Id);
        Assert.Equal(15m, saved.Price);
        Assert.Equal(20, saved.DurationMinutes);
    }

    [Fact]
    public async Task WaitingWriteRechecksDeactivationEvenWithAPreloadedStaffEntity()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        await Prepare(setup);
        var gate = new LockGate();
        await using var app = setup.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Configure(services, gate)));
        await using var owner = app.Services.CreateAsyncScope();
        await using var employee = app.Services.CreateAsyncScope();
        var cached = await employee.ServiceProvider.GetRequiredService<IStaffRepository>().GetByIdAsync(setup.Staff.Id);
        Assert.True(cached!.IsActive);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = await RunBlockedRace(
            () => owner.ServiceProvider.GetRequiredService<StaffManagementService>()
                .SetActiveAsync(new Actor(setup.Owner.Id), setup.Staff.Id, false, timeout.Token),
            () => employee.ServiceProvider.GetRequiredService<OfferingTermsService>()
                .UpdateAsync(new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, new(99m, 90), timeout.Token), gate, timeout);
        Assert.Null(errors[0]);
        var denied = Assert.IsType<AppException>(errors[1]);
        Assert.Equal(ErrorCodes.StaffInactive, denied.Code);
        Assert.Equal(AppErrorKind.Forbidden, denied.Kind);
        await using var verify = fixture.CreateContext();
        Assert.False((await verify.StaffMembers.SingleAsync(s => s.Id == setup.Staff.Id)).IsActive);
        var saved = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
        Assert.Equal(12.50m, saved.Price);
        Assert.Equal(30, saved.DurationMinutes);
    }

    [Fact]
    public async Task CancellingWhileWaitingLeavesTermsUnchangedAndReleasesTheTransaction()
    {
        await using var setup = await OfferingOverviewScenario.Create(fixture);
        var gate = new LockGate();
        await using var app = setup.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Configure(services, gate)));
        await using var holder = app.Services.CreateAsyncScope();
        await using var writer = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        async Task Hold()
        {
            await using var transaction = await holder.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginTransactionAsync(timeout.Token);
            await holder.ServiceProvider.GetRequiredService<IStaffOperationLock>().AcquireAsync(setup.Staff.Id, timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
        var errors = await RunBlockedRace(Hold,
            () => writer.ServiceProvider.GetRequiredService<OfferingTermsService>().UpdateAsync(
                new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, new(99m, 90), cancellation.Token),
            gate, timeout, () => cancellation.Cancel());
        Assert.Null(errors[0]);
        Assert.IsAssignableFrom<OperationCanceledException>(errors[1]);
        await using (var verify = fixture.CreateContext())
        {
            var unchanged = await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id);
            Assert.Equal(12.50m, unchanged.Price);
            Assert.Equal(30, unchanged.DurationMinutes);
        }
        await using var fresh = app.Services.CreateAsyncScope();
        var saved = await fresh.ServiceProvider.GetRequiredService<OfferingTermsService>().UpdateAsync(
            new Actor(setup.Employee.Id), setup.Staff.Id, setup.ActiveService.Id, new(40m, 50), timeout.Token);
        Assert.Equal(40m, saved.Price);
        Assert.Equal(50, saved.DurationMinutes);
    }

    private async Task<Exception?[]> RunBlockedRace(Func<Task> firstOperation, Func<Task> secondOperation,
        LockGate gate, CancellationTokenSource timeout, Action? afterBlocked = null)
    {
        var ct = timeout.Token;
        var first = firstOperation();
        Task? second = null;
        try
        {
            await Task.WhenAny(first, gate.Held.Task).WaitAsync(ct);
            if (first.IsCompleted) await first;
            var holderPid = await gate.Held.Task.WaitAsync(ct);
            second = secondOperation();
            await Task.WhenAny(second, gate.Waiter.Task).WaitAsync(ct);
            if (second.IsCompleted) await second;
            var waiterPid = await gate.Waiter.Task.WaitAsync(ct);
            await using var observer = new NpgsqlConnection(fixture.ConnectionString);
            await observer.OpenAsync(ct);
            while (true)
            {
                Assert.False(second.IsCompleted, "The second operation must wait for this employee's actual PostgreSQL lock.");
                await using var query = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", observer);
                query.Parameters.AddWithValue("holder", holderPid);
                query.Parameters.AddWithValue("waiter", waiterPid);
                if ((bool)(await query.ExecuteScalarAsync(ct))!) break;
                await Task.Delay(10, ct);
            }
            afterBlocked?.Invoke();
        }
        catch
        {
            await timeout.CancelAsync();
            gate.Release.TrySetResult();
            await Task.WhenAll(Capture(first), Capture(second ?? Task.CompletedTask));
            throw;
        }
        finally { gate.Release.TrySetResult(); }
        // Settle both operations before assertions or scope disposal, including expected rejections.
        return await Task.WhenAll(Capture(first), Capture(second!));
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try { await task; return null; }
        catch (Exception error) { return error; }
    }

    private sealed class LockGate
    {
        public TaskCompletionSource<int> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Waiter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;
    }

    private sealed class ObservedStaffLock(AppDbContext db, LockGate gate) : IStaffOperationLock
    {
        public async Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref gate.Attempts);
            await db.Database.OpenConnectionAsync(ct);
            var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            if (attempt == 2) gate.Waiter.TrySetResult(pid);
            await new PostgresStaffOperationLock(db).AcquireAsync(staffMemberId, ct);
            if (attempt == 1)
            {
                gate.Held.TrySetResult(pid);
                await gate.Release.Task.WaitAsync(ct);
            }
        }
    }

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
    }

    private static void Configure(IServiceCollection services, LockGate gate)
    {
        services.AddSingleton<IClock>(new Clock());
        services.AddScoped<IStaffOperationLock>(provider => new ObservedStaffLock(provider.GetRequiredService<AppDbContext>(), gate));
    }

    private async Task<DateTime> Prepare(OfferingOverviewScenario setup)
    {
        await using var db = fixture.CreateContext();
        var business = await db.BusinessSettings.SingleOrDefaultAsync();
        if (business is null) db.Add(business = new BusinessSettings("Studio", "Staff", "Asia/Yerevan", "AMD"));
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == business.Id)) db.Add(new BookingCoordination(business));
        db.AddRange(new BookingCoordination(setup.Staff), new WorkingHours(setup.Staff.Id, new(9, 0), new(18, 0)));
        await db.SaveChangesAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(new Clock().UtcNow, zone)).AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new(10, 0), DateTimeKind.Unspecified), zone);
    }
}
