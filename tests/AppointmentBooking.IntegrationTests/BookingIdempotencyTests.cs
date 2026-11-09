using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
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
        await using var host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(Clock)));
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookingService>()
            .CreateAsync(new Actor(Client.Id), request, default);
    }

    public static void ConfigureClock(IServiceCollection services) => services.AddSingleton(Clock);

    public ValueTask DisposeAsync() => Factory.DisposeAsync();

    private sealed class BookingClock : IClock
    {
        public DateTime UtcNow => new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    }
}
