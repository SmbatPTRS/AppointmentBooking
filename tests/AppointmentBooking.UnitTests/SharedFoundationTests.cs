using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Infrastructure;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.UnitTests;

public sealed class SharedFoundationTests
{
    [Fact]
    public void UserRepositorySharesTheScopedContextAndClockCanBeReplaced()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Database=not_opened;Username=test;Password=test"
            }).Build();
        var services = new ServiceCollection().AddInfrastructure(configuration);
        var clock = new FakeClock(new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc));
        services.AddSingleton<IClock>(clock);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider;
        var db = scoped.GetRequiredService<AppDbContext>();
        Assert.Same(db, scoped.GetRequiredService<IUnitOfWork>());
        var user = new User(123, "Test", null, clock.UtcNow);
        scoped.GetRequiredService<IUserRepository>().Add(user);
        Assert.Equal(EntityState.Added, db.Entry(user).State);
        Assert.NotNull(scoped.GetRequiredService<IBookingRepository>());
        Assert.NotNull(scoped.GetRequiredService<IStaffOperationLock>());
        Assert.Same(clock, scoped.GetRequiredService<IClock>());
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(clock.UtcNow, scoped.GetRequiredService<IClock>().UtcNow);
        using var secondScope = provider.CreateScope();
        Assert.NotSame(db, secondScope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public void ModelRegistersExactlyTheEightSharedEntities()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=not_opened;Username=test;Password=test").Options);
        var expected = new[] { typeof(User), typeof(StaffMember), typeof(BusinessSettings),
            typeof(Service), typeof(StaffService), typeof(WorkingHours), typeof(StaffDayOff), typeof(Booking) };
        Assert.Equal(expected.OrderBy(t => t.Name), db.Model.GetEntityTypes()
            .Select(t => t.ClrType).OrderBy(t => t.Name));
        Assert.All(db.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()),
            fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }

    [Fact]
    public void BookingSnapshotsAndLifecyclePreserveAcceptedTerms()
    {
        var now = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
        var offering = new StaffService(Guid.NewGuid(), Guid.NewGuid(), 25m, 30);
        var booking = new Booking(Guid.NewGuid(), offering.StaffMemberId, offering.Id,
            Guid.NewGuid(), now.AddHours(1), offering.Price, offering.DurationMinutes, now);
        offering.UpdateTerms(50m, 60);
        Assert.Equal(25m, booking.Price);
        Assert.Equal(30, booking.DurationMinutes);
        Assert.Equal(now.AddMinutes(90), booking.EndsAtUtc);
        Assert.Throws<InvalidOperationException>(() => booking.Complete(now.AddHours(1)));
        booking.Complete(booking.EndsAtUtc);
        booking.Complete(booking.EndsAtUtc.AddMinutes(1));
        Assert.Equal(BookingStatus.Completed, booking.Status);
        Assert.Equal(booking.EndsAtUtc, booking.CompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() => booking.Cancel(now));
    }
}
