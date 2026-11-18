using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BookingIdempotencyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task IdenticalRetryReturnsPersistedSnapshotAfterOfferingTermsChange()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var created = await seed.CreateBookingAsync(seed.Request);

        await using var db = fixture.CreateContext();
        var offering = await db.StaffServices.SingleAsync(x => x.Id == seed.Request.StaffServiceId);
        offering.UpdateTerms(40m, 45);
        await db.SaveChangesAsync();

        var retried = await seed.CreateBookingAsync(seed.Request);

        Assert.Equal(created, retried);
        var stored = await db.Bookings.SingleAsync(x => x.RequestId == seed.Request.RequestId);
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(25m, stored.Price);
        Assert.Equal(30, stored.DurationMinutes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedExpectedTermsConflictOnRetryAndOnNewRequest(bool changePrice)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var created = await seed.CreateBookingAsync(seed.Request);
        var changed = changePrice
            ? seed.Request with { ExpectedPrice = 26m }
            : seed.Request with { ExpectedDurationMinutes = 45 };

        var retry = await Assert.ThrowsAsync<AppException>(() => seed.CreateBookingAsync(changed));
        Assert.Equal(ErrorCodes.RequestIdConflict, retry.Code);

        var fresh = changed with { RequestId = Guid.NewGuid(), StartAtUtc = changed.StartAtUtc.AddHours(1) };
        var rejected = await Assert.ThrowsAsync<AppException>(() => seed.CreateBookingAsync(fresh));
        Assert.Equal(ErrorCodes.OfferingChanged, rejected.Code);

        await using var db = fixture.CreateContext();
        var stored = await db.Bookings.SingleAsync(x => x.ClientUserId == seed.Client.Id);
        Assert.Equal(created.Id, stored.Id);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsReturnTheSameIdAndPersistOneRow()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await start.Task;
            return await seed.CreateBookingAsync(seed.Request);
        }).ToArray();
        start.SetResult();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(results[0].Id, results[1].Id);
        await using var db = fixture.CreateContext();
        var rows = await db.Bookings.Where(b => b.ClientUserId == seed.Client.Id).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(results[0].Id, rows[0].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UniqueConflictIsRecoveredThroughAFreshScope(bool differentPayload)
    {
        var race = new RequestIdConflictProbe(fixture, differentPayload);
        await using var seed = await BookingTestSetup.CreateAsync(fixture, services =>
        {
            services.AddScoped<IUnitOfWork>(provider =>
                new InsertCompetingBookingOnSave(provider.GetRequiredService<AppDbContext>(), race));
            services.AddScoped<IBookingRepository>(provider =>
            {
                var db = provider.GetRequiredService<AppDbContext>();
                race.ObserveRepository(db);
                return new BookingRepository(db, provider.GetRequiredService<IClock>());
            });
        });

        if (differentPayload)
        {
            var error = await Assert.ThrowsAsync<AppException>(() => seed.CreateBookingAsync(seed.Request));
            Assert.Equal(ErrorCodes.RequestIdConflict, error.Code);
        }
        else
        {
            var result = await seed.CreateBookingAsync(seed.Request);
            Assert.Equal(race.Winner!.Id, result.Id);
            Assert.Equal(BookingStatus.Cancelled, result.Status);
            Assert.Equal(seed.Request.ExpectedPrice, result.Price);
            Assert.Equal(seed.Request.ExpectedDurationMinutes, result.DurationMinutes);
        }

        Assert.Equal(1, race.UniqueConflicts);
        Assert.Equal(1, race.RecoveryContexts);
        await using var db = fixture.CreateContext();
        var stored = Assert.Single(await db.Bookings.Where(b => b.ClientUserId == seed.Client.Id).ToListAsync());
        Assert.Equal(race.Winner!.Id, stored.Id);
        Assert.Equal(BookingStatus.Cancelled, stored.Status);
    }

    [Fact]
    public async Task ReusingRequestIdForAnotherEmployeeCannotCreateAnotherRow()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var created = await seed.CreateBookingAsync(seed.Request);
        await using var db = fixture.CreateContext();
        var originalOffering = await db.StaffServices.SingleAsync(x => x.Id == seed.Request.StaffServiceId);
        var staff = await db.StaffMembers.SingleAsync(x => x.Id == originalOffering.StaffMemberId);
        var otherUser = new User(8_100_000_000 + Random.Shared.NextInt64(1_000_000), "Other employee", null,
            DateTime.UtcNow);
        var otherStaff = new StaffMember(otherUser.Id, "Other staff");
        var otherOffering = new StaffService(otherStaff.Id, originalOffering.ServiceId, 25m, 30);
        db.AddRange(otherUser, otherStaff, otherOffering, new BookingCoordination(otherStaff),
            new WorkingHours(otherStaff.Id, new TimeOnly(9, 0), new TimeOnly(17, 0)));
        await db.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<AppException>(() => seed.CreateBookingAsync(seed.Request with
        {
            StaffServiceId = otherOffering.Id
        }));

        Assert.Equal(ErrorCodes.RequestIdConflict, conflict.Code);
        Assert.Equal(created.Id, await db.Bookings.Where(b => b.ClientUserId == seed.Client.Id)
            .Select(b => b.Id).SingleAsync());
    }

    [Fact]
    public async Task RetryAfterCancellationReturnsTheOriginalPersistedIntent()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var created = await seed.CreateBookingAsync(seed.Request);
        await using (var db = fixture.CreateContext())
        {
            var booking = await db.Bookings.SingleAsync(b => b.Id == created.Id);
            booking.Cancel(new DateTime(2026, 11, 9, 8, 30, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        var retried = await seed.CreateBookingAsync(seed.Request);

        Assert.Equal(created.Id, retried.Id);
        Assert.Equal(BookingStatus.Cancelled, retried.Status);
        await using var verification = fixture.CreateContext();
        Assert.Single(await verification.Bookings.Where(b => b.ClientUserId == seed.Client.Id).ToListAsync());
    }
}

internal sealed class BookingTestSetup(
    ApiFactory factory, WebApplicationFactory<Program> host, User client, CreateBookingRequest request) : IAsyncDisposable
{
    private static long nextTelegramId = 8_100_000_000;
    private static readonly IClock Clock = new BookingClock();

    public ApiFactory Factory { get; } = factory;
    public User Client { get; } = client;
    public CreateBookingRequest Request { get; } = request;

    public static async Task<BookingTestSetup> CreateAsync(
        PostgresFixture fixture, Action<IServiceCollection>? configureServices = null)
    {
        await using var db = fixture.CreateContext();
        await new DatabaseSeeder(db).SeedBusinessSettingsAsync(
            new BusinessSettings("Studio", "Staff", "UTC", "AMD"), default);
        var client = new User(Interlocked.Increment(ref nextTelegramId), "Booking client", null, Clock.UtcNow);
        var employee = new User(Interlocked.Increment(ref nextTelegramId), "Booking employee", null, Clock.UtcNow);
        var staff = new StaffMember(employee.Id, "Staff");
        var service = new Service("Booking test consultation");
        var offering = new StaffService(staff.Id, service.Id, 25m, 30);
        db.AddRange(client, employee, staff, service, offering, new BookingCoordination(staff),
            new WorkingHours(staff.Id, new TimeOnly(9, 0), new TimeOnly(17, 0)));
        await db.SaveChangesAsync();

        var factory = new ApiFactory(fixture.ConnectionString);
        var request = new CreateBookingRequest(Guid.NewGuid(), employee.Id, offering.Id,
            Clock.UtcNow.AddHours(2), 25m, 30);
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            ConfigureClock(services);
            configureServices?.Invoke(services);
        }));
        try
        {
            // Start once, before any concurrent operation. Each call below still owns
            // its own scope/context; only the initialized host is shared.
            _ = host.Services;
            return new BookingTestSetup(factory, host, client, request);
        }
        catch
        {
            await host.DisposeAsync();
            await factory.DisposeAsync();
            throw;
        }
    }

    public Task<BookingDto> CreateBookingAsync(CreateBookingRequest request) =>
        CreateBookingAsync(new Actor(Client.Id), request);

    public async Task<BookingDto> CreateBookingAsync(Actor actor, CreateBookingRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookingService>()
            .CreateAsync(actor, request, default);
    }

    public static void ConfigureClock(IServiceCollection services) => services.AddSingleton(Clock);

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await Factory.DisposeAsync();
    }

    private sealed class BookingClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    }
}

// Test-only injection at the last possible point: all duplicate lookups and
// availability checks have finished, but the losing INSERT has not run yet.
internal sealed class InsertCompetingBookingOnSave(AppDbContext db, RequestIdConflictProbe race) : IUnitOfWork
{
    public Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct) => db.BeginTransactionAsync(ct);

    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        var pending = db.ChangeTracker.Entries<Booking>()
            .Single(entry => entry.State == EntityState.Added).Entity;
        await race.InsertWinnerAsync(db, pending, ct);
        try
        {
            return await ((IUnitOfWork)db).SaveChangesAsync(ct);
        }
        catch (DbConflictException error) when (error.Kind == ConflictKind.UniqueRequestId)
        {
            var updateError = Assert.IsType<DbUpdateException>(error.InnerException);
            var postgresError = Assert.IsType<PostgresException>(updateError.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
            Assert.Equal("ux_bookings_client_request", postgresError.ConstraintName);
            race.UniqueConflicts++;
            throw;
        }
    }
}

internal sealed class RequestIdConflictProbe(PostgresFixture fixture, bool differentPayload)
{
    private AppDbContext? failedContext;
    public Booking? Winner { get; private set; }
    public int UniqueConflicts { get; set; }
    public int RecoveryContexts { get; private set; }

    public async Task InsertWinnerAsync(AppDbContext loser, Booking pending, CancellationToken ct)
    {
        Assert.Null(Winner);
        Assert.NotNull(loser.Database.CurrentTransaction);
        failedContext = loser;
        await using var writer = fixture.CreateContext();
        Winner = new Booking(pending.ClientUserId, pending.StaffMemberId, pending.StaffServiceId,
            pending.RequestId, pending.StartsAtUtc, pending.Price + (differentPayload ? 1m : 0m),
            pending.DurationMinutes, pending.CreatedAtUtc);
        // A cancelled winner keeps the durable RequestId but removes overlap as a
        // competing constraint. The loser must hit the unique key, not exclusion.
        Winner.Cancel(pending.CreatedAtUtc);
        writer.Bookings.Add(Winner);
        await writer.SaveChangesAsync(ct);
    }

    public void ObserveRepository(AppDbContext context)
    {
        if (UniqueConflicts == 0) return;
        Assert.NotNull(failedContext);
        Assert.Null(failedContext.Database.CurrentTransaction);
        Assert.NotEqual(failedContext.ContextId, context.ContextId);
        Assert.Null(context.Database.CurrentTransaction);
        RecoveryContexts++;
    }
}
