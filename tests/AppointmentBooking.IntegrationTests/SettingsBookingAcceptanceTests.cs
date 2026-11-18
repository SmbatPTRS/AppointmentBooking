using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.Infrastructure.Transactions;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AppointmentBooking.IntegrationTests;

public sealed class SettingsBookingAcceptanceTests
{
    private static readonly IClock Clock = new FixedClock();

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    public async Task EveryBookingStatusLocksRegionalSettingsButAllowsLabels(BookingStatus status)
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var seed = await SeedAsync(fixture);
        await using (var setup = fixture.CreateContext())
        {
            var history = new Booking(seed.Actor.UserId, seed.StaffId, seed.Request.StaffServiceId,
                Guid.NewGuid(), Clock.UtcNow.AddHours(-2), 25m, 30, Clock.UtcNow.AddDays(-1));
            if (status == BookingStatus.Cancelled) history.Cancel(Clock.UtcNow.AddHours(-3));
            if (status == BookingStatus.Completed) history.Complete(Clock.UtcNow);
            setup.Bookings.Add(history);
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var settings = Settings(db);
        var error = await Assert.ThrowsAsync<AppException>(() => settings.UpdateTimeZoneAndCurrencyAsync(
            seed.Actor, "Asia/Yerevan", "USD"));
        Assert.Equal(ErrorCodes.BusinessSettingsLocked, error.Code);
        Assert.Equal(AppErrorKind.Conflict, error.Kind);
        await settings.UpdateLabelsAsync(seed.Actor, "Owner-edited studio", "Specialist");
        await using var verify = fixture.CreateContext();
        var stored = await verify.BusinessSettings.SingleAsync();
        Assert.Equal("Owner-edited studio", stored.DisplayName);
        Assert.Equal("Specialist", stored.StaffLabel);
        Assert.Equal("UTC", stored.TimeZoneId);
        Assert.Equal("AMD", stored.CurrencyCode);
        Assert.Equal(1L, (await verify.BookingCoordinationRows.SingleAsync(row => row.BusinessSettingsId == 1)).ChangeVersion);
        var booking = await verify.Bookings.SingleAsync();
        Assert.Equal(status, booking.Status);
        Assert.Equal(25m, booking.Price);
        Assert.Equal(30, booking.DurationMinutes);
    }

    [Theory]
    [InlineData("timezone")]
    [InlineData("currency")]
    [InlineData("invalid-currency")]
    [InlineData("labels")]
    public async Task OwnerSubmissionWhileBookingIsInFlightUsesCoordinationVersion(string operation)
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var seed = await SeedAsync(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var bookingDb = fixture.CreateContext();
        await using var ownerDb = fixture.CreateContext();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var gate = new PausingStaffLock(new PostgresStaffOperationLock(bookingDb));
        var service = Booking(bookingDb, gate, new BookingCoordinationRepository(bookingDb), provider);
        var pending = service.CreateAsync(seed.Actor, seed.Request, ct);
        try
        {
            await gate.Acquired.Task.WaitAsync(ct);
            var settings = Settings(ownerDb);
            switch (operation)
            {
                case "timezone": await settings.UpdateTimeZoneAndCurrencyAsync(seed.Actor, "Asia/Yerevan", "AMD", ct); break;
                case "currency": await settings.UpdateTimeZoneAndCurrencyAsync(seed.Actor, "UTC", "USD", ct); break;
                case "invalid-currency":
                    var invalid = await Assert.ThrowsAsync<AppException>(() => settings.UpdateTimeZoneAndCurrencyAsync(seed.Actor, "UTC", "US", ct));
                    Assert.Equal(ErrorCodes.InvalidCurrency, invalid.Code);
                    break;
                case "labels": await settings.UpdateLabelsAsync(seed.Actor, "New studio", "Specialist", ct); break;
            }
        }
        finally { gate.Resume.TrySetResult(); }
        if (operation == "labels")
            Assert.Equal(BookingStatus.Confirmed, (await pending).Status);
        else
        {
            var error = await Assert.ThrowsAsync<AppException>(() => pending);
            Assert.Equal(ErrorCodes.ConfigurationChanged, error.Code);
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(operation == "labels" ? 1 : 0, await verify.Bookings.CountAsync(ct));
        Assert.Equal(operation == "labels" ? 0L : 1L,
            (await verify.BookingCoordinationRows.SingleAsync(row => row.BusinessSettingsId == 1, ct)).ChangeVersion);
        var persisted = await verify.BusinessSettings.SingleAsync(ct);
        Assert.Equal(operation == "timezone" ? "Asia/Yerevan" : "UTC", persisted.TimeZoneId);
        Assert.Equal(operation == "currency" ? "USD" : "AMD", persisted.CurrencyCode);
        Assert.Equal(operation == "labels" ? "New studio" : "Studio", persisted.DisplayName);
    }

    [Fact]
    public async Task BookingHoldingBusinessLockCommitsBeforeWaitingRegionalEditIsRejected()
    {
        await using var fixture = new PostgresFixture();
        await fixture.InitializeAsync();
        var seed = await SeedAsync(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var bookingDb = fixture.CreateContext();
        await using var ownerDb = fixture.CreateContext();
        await ownerDb.Database.OpenConnectionAsync(ct);
        var ownerPid = ((NpgsqlConnection)ownerDb.Database.GetDbConnection()).ProcessID;
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var gate = new PausingCoordination(new BookingCoordinationRepository(bookingDb));
        var booking = Booking(bookingDb, new PostgresStaffOperationLock(bookingDb), gate, provider)
            .CreateAsync(seed.Actor, seed.Request, ct);
        Task<BusinessSettingsView>? edit = null;
        try
        {
            await gate.Acquired.Task.WaitAsync(ct);
            edit = Settings(ownerDb).UpdateTimeZoneAndCurrencyAsync(seed.Actor, "Asia/Yerevan", "USD", ct);
            // Observe a real PostgreSQL wait, rather than assuming task timing proves blocking.
            await using var observer = new NpgsqlConnection(fixture.ConnectionString);
            await observer.OpenAsync(ct);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await using var waiting = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", observer);
                waiting.Parameters.AddWithValue("pid", ownerPid);
                if ((bool)(await waiting.ExecuteScalarAsync(ct))!) break;
                await Task.Delay(25, ct);
            }
            Assert.False(edit.IsCompleted);
        }
        finally { gate.Resume.TrySetResult(); }
        var created = await booking;
        Assert.NotNull(edit);
        var rejected = await Assert.ThrowsAsync<AppException>(() => edit!);
        Assert.Equal(ErrorCodes.BusinessSettingsLocked, rejected.Code);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Bookings.SingleAsync(ct);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(25m, stored.Price);
        Assert.Equal(30, stored.DurationMinutes);
        Assert.Equal(seed.Request.StartAtUtc.AddMinutes(30), stored.EndsAtUtc);
        var settings = await verify.BusinessSettings.SingleAsync(ct);
        Assert.Equal("UTC", settings.TimeZoneId);
        Assert.Equal("AMD", settings.CurrencyCode);
        Assert.Equal(1L, (await verify.BookingCoordinationRows.SingleAsync(row => row.BusinessSettingsId == 1, ct)).ChangeVersion);
    }

    private static AccessService Access(AppDbContext db) => new(new UserRepository(db), new StaffRepository(db), 701);
    private static BusinessSettingsService Settings(AppDbContext db) => new(Access(db), new BusinessSettingsRepository(db),
        new BookingRepository(db, Clock), new BookingCoordinationRepository(db), db);

    private static BookingService Booking(AppDbContext db, IStaffOperationLock staffLock,
        IBookingCoordinationRepository coordination, ServiceProvider provider)
    {
        var catalog = new CatalogRepository(db);
        var bookings = new BookingRepository(db, Clock);
        var settings = new BusinessSettingsRepository(db);
        var availability = new AvailabilityService(catalog, new StaffRepository(db), new ScheduleRepository(db),
            settings, bookings, Clock, new AvailabilityCalculator(new BookingOptions()));
        return new BookingService(bookings, provider.GetRequiredService<IServiceScopeFactory>(), Access(db),
            catalog, db, staffLock, availability, Clock, coordination, settings);
    }

    private static async Task<Seed> SeedAsync(PostgresFixture fixture)
    {
        await using var db = fixture.CreateContext();
        await new DatabaseSeeder(db).SeedBusinessSettingsAsync(new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        var user = new User(701, "Owner", null, Clock.UtcNow);
        var staff = new StaffMember(user.Id, "Staff");
        var service = new Service("Consultation");
        var offering = new StaffService(staff.Id, service.Id, 25m, 30);
        db.AddRange(user, staff, service, offering, new BookingCoordination(staff),
            new WorkingHours(staff.Id, new(9, 0), new(17, 0)));
        await db.SaveChangesAsync();
        return new Seed(new Actor(user.Id), staff.Id,
            new CreateBookingRequest(Guid.NewGuid(), user.Id, offering.Id, Clock.UtcNow.AddHours(2), 25m, 30));
    }
    private sealed record Seed(Actor Actor, Guid StaffId, CreateBookingRequest Request);
    private sealed class FixedClock : IClock { public DateTime UtcNow => new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc); }
    private sealed class PausingStaffLock(IStaffOperationLock inner) : IStaffOperationLock
    {
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AcquireAsync(Guid id, CancellationToken ct)
        {
            await inner.AcquireAsync(id, ct);
            Acquired.TrySetResult();
            await Resume.Task.WaitAsync(ct);
        }
    }
    private sealed class PausingCoordination(IBookingCoordinationRepository inner) : IBookingCoordinationRepository
    {
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BookingCoordinationVersions> ReadVersionsAsync(Guid id, CancellationToken ct) => inner.ReadVersionsAsync(id, ct);
        public Task IncrementBusinessVersionAsync(CancellationToken ct) => inner.IncrementBusinessVersionAsync(ct);
        public Task IncrementStaffVersionAsync(Guid id, CancellationToken ct) => inner.IncrementStaffVersionAsync(id, ct);
        public Task LockBusinessAsync(CancellationToken ct) => inner.LockBusinessAsync(ct);
        public async Task<BookingCoordinationVersions> LockAndReadVersionsAsync(Guid id, CancellationToken ct)
        {
            var versions = await inner.LockAndReadVersionsAsync(id, ct);
            Acquired.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return versions;
        }
    }
}
