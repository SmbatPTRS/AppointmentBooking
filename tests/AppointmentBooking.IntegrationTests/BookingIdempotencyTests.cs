using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
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

    [Fact]
    public async Task UniqueConflictIsRecoveredThroughAFreshScope()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var lockGate = new ReleaseTogetherLock(2);
        var tasks = new[]
        {
            seed.CreateBookingAsync(seed.Request, lockGate),
            seed.CreateBookingAsync(seed.Request, lockGate)
        };

        var results = await Task.WhenAll(tasks);

        Assert.Equal(results[0].Id, results[1].Id);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Bookings.Where(b => b.RequestId == seed.Request.RequestId).ToListAsync());
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
    ApiFactory factory, User client, CreateBookingRequest request) : IAsyncDisposable
{
    private static long nextTelegramId = 8_100_000_000;
    private static readonly IClock Clock = new BookingClock();

    public ApiFactory Factory { get; } = factory;
    public User Client { get; } = client;
    public CreateBookingRequest Request { get; } = request;

    public static async Task<BookingTestSetup> CreateAsync(PostgresFixture fixture)
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
        return new BookingTestSetup(factory, client, request);
    }

    public async Task<BookingDto> CreateBookingAsync(CreateBookingRequest request)
    {
        return await CreateBookingAsync(new Actor(Client.Id), request, null);
    }

    public async Task<BookingDto> CreateBookingAsync(Actor actor, CreateBookingRequest request)
    {
        return await CreateBookingAsync(actor, request, null);
    }

    public async Task<BookingDto> CreateBookingAsync(
        CreateBookingRequest request,
        IStaffOperationLock? staffLock)
    {
        return await CreateBookingAsync(new Actor(Client.Id), request, staffLock);
    }

    private async Task<BookingDto> CreateBookingAsync(
        Actor actor,
        CreateBookingRequest request,
        IStaffOperationLock? staffLock)
    {
        await using var host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Clock);
                if (staffLock is not null)
                    services.AddSingleton<IStaffOperationLock>(staffLock);
            }));
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookingService>()
            .CreateAsync(actor, request, default);
    }

    public static void ConfigureClock(IServiceCollection services) => services.AddSingleton(Clock);

    public ValueTask DisposeAsync() => Factory.DisposeAsync();

    private sealed class BookingClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    }
}

internal sealed class ReleaseTogetherLock(int parties) : IStaffOperationLock
{
    private readonly TaskCompletionSource allArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    public Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
    {
        if (Interlocked.Increment(ref arrived) == parties)
            allArrived.TrySetResult();

        return allArrived.Task.WaitAsync(ct);
    }
}
