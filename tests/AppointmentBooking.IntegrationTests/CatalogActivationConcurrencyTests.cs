using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogActivationConcurrencyTests(PostgresFixture fixture)
{
    // Missing publication lets an old confirmation commit. Publishing only after the
    // staff lock deadlocks this ordering; holding the final version lock must still win.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BookingAndDeactivationHonorTheFinalCoordinationLock(bool shared, bool bookingPassedFinalCheck)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        var start = await PrepareHours(setup);
        var before = await Versions(setup.Staff.Id);
        var otherBefore = await Versions(setup.Other.Id);
        var gate = new RaceGate(setup.Staff.Id, bookingPassedFinalCheck ? PausePoint.FinalCheck : PausePoint.StaffLock);
        await using var app = Configure(setup, gate);
        await using var bookingScope = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var request = new CreateBookingRequest(Guid.NewGuid(), setup.Customer.Id, setup.Active.Id, start, 12.50m, 30);
        var booking = bookingScope.ServiceProvider.GetRequiredService<BookingService>()
            .CreateAsync(new Actor(setup.Customer.Id), request, ct);
        Task? deactivation = null;
        Exception? bookingError = null;
        Exception? mutationError = null;
        try
        {
            var holder = await AwaitSignal(booking, gate.Held.Task, ct);
            deactivation = SetActive(app, setup, shared, false, ct);
            var waiter = await AwaitSignal(deactivation, gate.Waiter.Task, ct);
            await AssertBlockedBy(waiter, holder, deactivation, ct);
            var during = await Versions(setup.Staff.Id);
            Assert.Equal(before.BusinessVersion, during.BusinessVersion);
            Assert.Equal(before.StaffVersion + (bookingPassedFinalCheck ? 0 : 1), during.StaffVersion);
            await using var verify = fixture.CreateContext();
            Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id, ct)).IsActive);
            Assert.True((await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id, ct)).IsActive);
        }
        catch
        {
            await timeout.CancelAsync();
            throw;
        }
        finally
        {
            gate.Release.TrySetResult();
            var errors = await Task.WhenAll(Capture(booking), Capture(deactivation ?? Task.CompletedTask));
            bookingError = errors[0];
            mutationError = errors[1];
        }
        Assert.Null(mutationError);
        var after = await Versions(setup.Staff.Id);
        Assert.Equal(before.BusinessVersion, after.BusinessVersion);
        Assert.Equal(before.StaffVersion + 1, after.StaffVersion);
        Assert.Equal(otherBefore, await Versions(setup.Other.Id));
        await using var stored = fixture.CreateContext();
        Assert.Equal(!shared, (await stored.Services.SingleAsync(s => s.Id == setup.ActiveService.Id, ct)).IsActive);
        Assert.Equal(shared, (await stored.StaffServices.SingleAsync(o => o.Id == setup.Active.Id, ct)).IsActive);
        var rows = await stored.Bookings.Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync(ct);
        if (!bookingPassedFinalCheck)
        {
            AssertConfigurationChanged(bookingError);
            Assert.Empty(rows);
        }
        else
        {
            Assert.Null(bookingError);
            var accepted = await booking;
            var saved = Assert.Single(rows);
            Assert.Equal(accepted.Id, saved.Id);
            Assert.Equal(setup.Active.Id, saved.StaffServiceId);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
            Assert.Equal(12.50m, saved.Price);
            Assert.Equal(30, saved.DurationMinutes);
            Assert.Equal(start, saved.StartsAtUtc);
            Assert.Equal(start.AddMinutes(30), saved.EndsAtUtc);
            // An accepted idempotent replay remains recoverable after eligibility changes;
            // a new request is rejected even though it carries identical terms and time.
            await using var retryScope = app.Services.CreateAsyncScope();
            var bookings = retryScope.ServiceProvider.GetRequiredService<BookingService>();
            var replay = await bookings.CreateAsync(new Actor(setup.Customer.Id), request, ct);
            Assert.Equal(accepted.Id, replay.Id);
            Assert.Equal(12.50m, replay.Price);
            var inactive = await Assert.ThrowsAsync<AppException>(() => bookings.CreateAsync(
                new Actor(setup.Customer.Id), request with { RequestId = Guid.NewGuid() }, ct));
            Assert.Equal(ErrorCodes.OfferingInactive, inactive.Code);
            Assert.Equal(1, await stored.Bookings.CountAsync(b => b.StaffMemberId == setup.Staff.Id, ct));
        }
    }

    // Both flags are active again: only the published version distinguishes this old
    // intent from a fresh customer confirmation. A flag-only implementation must fail.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationThenReactivationRejectsOldIntentAndAllowsAFreshConfirmation(bool shared)
    {
        await using var setup = await CatalogActivationScenario.Create(fixture);
        var start = await PrepareHours(setup);
        var before = await Versions(setup.Staff.Id);
        var gate = new RaceGate(setup.Staff.Id, PausePoint.VersionRead);
        await using var app = Configure(setup, gate);
        await using var bookingScope = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var request = new CreateBookingRequest(Guid.NewGuid(), setup.Customer.Id, setup.Active.Id, start, 12.50m, 30);
        var booking = bookingScope.ServiceProvider.GetRequiredService<BookingService>()
            .CreateAsync(new Actor(setup.Customer.Id), request, ct);
        Exception? bookingError = null;
        try
        {
            await AwaitSignal(booking, gate.Held.Task, ct);
            await SetActive(app, setup, shared, false, ct);
            await SetActive(app, setup, shared, true, ct);
            Assert.False(booking.IsCompleted);
            await using var verify = fixture.CreateContext();
            Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.ActiveService.Id, ct)).IsActive);
            Assert.True((await verify.StaffServices.SingleAsync(o => o.Id == setup.Active.Id, ct)).IsActive);
        }
        catch
        {
            await timeout.CancelAsync();
            throw;
        }
        finally
        {
            gate.Release.TrySetResult();
            bookingError = await Capture(booking);
        }
        AssertConfigurationChanged(bookingError);
        var after = await Versions(setup.Staff.Id);
        Assert.Equal(before.BusinessVersion, after.BusinessVersion);
        Assert.Equal(before.StaffVersion + 1, after.StaffVersion);
        await using var stored = fixture.CreateContext();
        Assert.Empty(await stored.Bookings.Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync(ct));
        await using var fresh = app.Services.CreateAsyncScope();
        var accepted = await fresh.ServiceProvider.GetRequiredService<BookingService>().CreateAsync(
            new Actor(setup.Customer.Id), request with { RequestId = Guid.NewGuid() }, ct);
        Assert.Equal(12.50m, accepted.Price);
        Assert.Equal(30, accepted.DurationMinutes);
        var saved = Assert.Single(await stored.Bookings.AsNoTracking().Where(b => b.StaffMemberId == setup.Staff.Id).ToListAsync(ct));
        Assert.Equal(accepted.Id, saved.Id);
        Assert.Equal(start, saved.StartsAtUtc);
        Assert.Equal(start.AddMinutes(30), saved.EndsAtUtc);
    }

    private static void AssertConfigurationChanged(Exception? error)
    {
        var conflict = Assert.IsType<AppException>(error);
        Assert.Equal(ErrorCodes.ConfigurationChanged, conflict.Code);
        Assert.Equal(AppErrorKind.Conflict, conflict.Kind);
    }

    private static WebApplicationFactory<Program> Configure(OfferingOverviewScenario setup, RaceGate gate) =>
        setup.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClock>(new Clock());
            services.AddScoped<IStaffOperationLock>(p => new ObservedStaffLock(p.GetRequiredService<AppDbContext>(), gate));
            services.AddScoped<IBookingCoordinationRepository>(p => new ObservedCoordination(p.GetRequiredService<AppDbContext>(), gate));
        }));

    private static async Task SetActive(WebApplicationFactory<Program> app, OfferingOverviewScenario setup,
        bool shared, bool active, CancellationToken ct)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var actor = new Actor(setup.Owner.Id);
        if (shared)
            await scope.ServiceProvider.GetRequiredService<SharedServiceActivationService>().SetActiveAsync(actor, setup.ActiveService.Id, active, ct);
        else
            await scope.ServiceProvider.GetRequiredService<OfferingActivationService>().SetActiveAsync(actor, setup.Staff.Id, setup.ActiveService.Id, active, ct);
    }

    private async Task<DateTime> PrepareHours(OfferingOverviewScenario setup)
    {
        await using var db = fixture.CreateContext();
        db.Add(new WorkingHours(setup.Staff.Id, new(9, 0), new(18, 0)));
        await db.SaveChangesAsync();
        var business = await db.BusinessSettings.SingleAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId);
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(new Clock().UtcNow, zone)).AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(tomorrow.ToDateTime(new(10, 0), DateTimeKind.Unspecified), zone);
    }

    private async Task<BookingCoordinationVersions> Versions(Guid staffId)
    {
        await using var db = fixture.CreateContext();
        return await new BookingCoordinationRepository(db).ReadVersionsAsync(staffId, default);
    }

    private static async Task<int> AwaitSignal(Task operation, Task<int> signal, CancellationToken ct)
    {
        await Task.WhenAny(operation, signal).WaitAsync(ct);
        if (operation.IsCompleted)
        {
            await operation;
            Assert.Fail("Operation completed before reaching the expected concurrency barrier.");
        }
        return await signal.WaitAsync(ct);
    }

    private async Task AssertBlockedBy(int waiter, int holder, Task operation, CancellationToken ct)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(ct);
        while (true)
        {
            Assert.False(operation.IsCompleted, "The mutation must wait for the booking's real PostgreSQL lock.");
            await using var query = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", observer);
            query.Parameters.AddWithValue("holder", holder);
            query.Parameters.AddWithValue("waiter", waiter);
            if ((bool)(await query.ExecuteScalarAsync(ct))!) return;
            await Task.Delay(10, ct);
        }
    }

    private static async Task<Exception?> Capture(Task operation)
    {
        try { await operation; return null; }
        catch (Exception error) { return error; }
    }

    private enum PausePoint { StaffLock, FinalCheck, VersionRead }

    private sealed class RaceGate(Guid staffId, PausePoint point)
    {
        public Guid StaffId { get; } = staffId;
        public PausePoint Point { get; } = point;
        public TaskCompletionSource<int> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Waiter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StaffAttempts;
        public int VersionReads;
        public async Task Pause(AppDbContext db, CancellationToken ct)
        {
            await db.Database.OpenConnectionAsync(ct);
            Held.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            await Release.Task.WaitAsync(ct);
        }
    }

    // Decorators retain real PostgreSQL operations; only their progress is controlled.
    private sealed class ObservedStaffLock(AppDbContext db, RaceGate gate) : IStaffOperationLock
    {
        public async Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
        {
            var attempt = staffMemberId == gate.StaffId ? Interlocked.Increment(ref gate.StaffAttempts) : 0;
            if (gate.Point == PausePoint.StaffLock && attempt == 2)
            {
                await db.Database.OpenConnectionAsync(ct);
                gate.Waiter.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            }
            await new PostgresStaffOperationLock(db).AcquireAsync(staffMemberId, ct);
            if (gate.Point == PausePoint.StaffLock && attempt == 1) await gate.Pause(db, ct);
        }
    }

    private sealed class ObservedCoordination(AppDbContext db, RaceGate gate) : IBookingCoordinationRepository
    {
        private readonly BookingCoordinationRepository real = new(db);
        public async Task<BookingCoordinationVersions> ReadVersionsAsync(Guid staffMemberId, CancellationToken ct)
        {
            var versions = await real.ReadVersionsAsync(staffMemberId, ct);
            if (gate.Point == PausePoint.VersionRead && staffMemberId == gate.StaffId &&
                Interlocked.Increment(ref gate.VersionReads) == 1) await gate.Pause(db, ct);
            return versions;
        }
        public async Task<BookingCoordinationVersions> LockAndReadVersionsAsync(Guid staffMemberId, CancellationToken ct)
        {
            var versions = await real.LockAndReadVersionsAsync(staffMemberId, ct);
            if (gate.Point == PausePoint.FinalCheck && staffMemberId == gate.StaffId) await gate.Pause(db, ct);
            return versions;
        }
        public async Task IncrementStaffVersionAsync(Guid staffMemberId, CancellationToken ct)
        {
            if (gate.Point == PausePoint.FinalCheck && staffMemberId == gate.StaffId)
            {
                await db.Database.OpenConnectionAsync(ct);
                gate.Waiter.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            }
            await real.IncrementStaffVersionAsync(staffMemberId, ct);
        }
        public Task IncrementBusinessVersionAsync(CancellationToken ct) => real.IncrementBusinessVersionAsync(ct);
        public Task LockBusinessAsync(CancellationToken ct) => real.LockBusinessAsync(ct);
    }

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
    }
}
