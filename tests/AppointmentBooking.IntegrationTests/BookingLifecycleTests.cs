using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BookingLifecycleTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedActorsCannotChangeBookingBeforeOrAfterTransition(bool complete)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed);
        var stranger = new Actor(Guid.NewGuid());
        var actor = complete ? await GetStaffActorAsync(booking) : new Actor(booking.ClientUserId);
        var now = complete ? booking.EndsAtUtc : booking.StartsAtUtc.AddSeconds(-1);

        if (complete)
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                TransitionAsync(seed, booking, stranger, now, true));
            Assert.Equal(ErrorCodes.Unauthorized, error.Code);
            Assert.Equal(AppErrorKind.Unauthorized, error.Kind);
        }
        else
        {
            Assert.Null(await TransitionAsync(seed, booking, stranger, now, false));
        }

        Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(booking.Id)).Status);

        var changed = await TransitionAsync(seed, booking, actor, now, complete);
        Assert.NotNull(changed);
        if (complete)
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                TransitionAsync(seed, booking, stranger, now.AddHours(1), true));
            Assert.Equal(ErrorCodes.Unauthorized, error.Code);
            Assert.Equal(AppErrorKind.Unauthorized, error.Kind);
        }
        else
        {
            Assert.Null(await TransitionAsync(seed, booking, stranger, now.AddHours(1), false));
        }
        Assert.Equal(changed.Status, (await ReadBookingAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task ExistingUnrelatedUserCannotCompleteBooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var otherSeed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed);
        var unrelated = new Actor(otherSeed.Client.Id);

        var error = await Assert.ThrowsAsync<AppException>(() =>
            TransitionAsync(seed, booking, unrelated, booking.EndsAtUtc, true));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Equal(AppErrorKind.Forbidden, error.Kind);
        Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task DeactivatedEmployeeCanCompleteOwnAppointmentAfterItEnds()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed);
        var staff = await GetStaffActorAsync(booking);

        await using (var db = fixture.CreateContext())
        {
            var member = await db.StaffMembers
                .SingleAsync(member => member.Id == booking.StaffMemberId);
            member.Deactivate();
            await db.SaveChangesAsync();
        }

        var completed = await TransitionAsync(
            seed, booking, staff, booking.EndsAtUtc, complete: true);

        Assert.NotNull(completed);
        Assert.Equal(BookingStatus.Completed, completed.Status);
        Assert.Equal(BookingStatus.Completed, (await ReadBookingAsync(booking.Id)).Status);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    public async Task IneligibleTimeLeavesBookingConfirmed(bool complete, int secondsFromBoundary)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed);
        var actor = complete ? await GetStaffActorAsync(booking) : new Actor(booking.ClientUserId);
        var boundary = complete ? booking.EndsAtUtc : booking.StartsAtUtc;

        if (complete)
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                TransitionAsync(seed, booking, actor, boundary.AddSeconds(secondsFromBoundary), true));
            Assert.Equal(ErrorCodes.BookingCannotBeCompleted, error.Code);
            Assert.Equal(AppErrorKind.Conflict, error.Kind);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                TransitionAsync(seed, booking, actor, boundary.AddSeconds(secondsFromBoundary), false));
            Assert.Equal(ErrorCodes.BookingCannotBeCancelled, error.Code);
            Assert.Equal(AppErrorKind.Conflict, error.Kind);
        }

        var stored = await ReadBookingAsync(booking.Id);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Null(stored.CancelledAtUtc);
        Assert.Null(stored.CompletedAtUtc);
    }

    [Theory]
    [InlineData(false, -1, BookingStatus.Cancelled)]
    [InlineData(true, 0, BookingStatus.Completed)]
    public async Task RepeatPreservesFirstTransition(
        bool complete, int secondsFromBoundary, BookingStatus expected)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed);
        var client = new Actor(booking.ClientUserId);
        var staff = await GetStaffActorAsync(booking);
        var actor = complete ? staff : client;
        var boundary = complete ? booking.EndsAtUtc : booking.StartsAtUtc;
        var firstTime = boundary.AddSeconds(secondsFromBoundary);
        var retryTime = booking.EndsAtUtc.AddHours(1);

        var first = await TransitionAsync(seed, booking, actor, firstTime, complete);
        Assert.NotNull(first);
        Assert.Equal(expected, first.Status);
        Assert.Equal(first, await TransitionAsync(seed, booking, actor, retryTime, complete));

        var stored = await ReadBookingAsync(booking.Id);
        Assert.Equal(expected, stored.Status);
        Assert.Equal(booking.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(complete ? null : firstTime, stored.CancelledAtUtc);
        Assert.Equal(complete ? firstTime : null, stored.CompletedAtUtc);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    public async Task TerminalBookingRejectsOppositeTransition(BookingStatus status)
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed, status);
        var complete = status == BookingStatus.Cancelled;
        var actor = complete ? await GetStaffActorAsync(booking) : new Actor(booking.ClientUserId);
        var now = complete ? booking.EndsAtUtc : booking.StartsAtUtc.AddSeconds(-1);

        if (complete)
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                TransitionAsync(seed, booking, actor, now, true));
            Assert.Equal(ErrorCodes.BookingCannotBeCompleted, error.Code);
            Assert.Equal(AppErrorKind.Conflict, error.Kind);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AppException>(() =>
                TransitionAsync(seed, booking, actor, now, false));
            Assert.Equal(ErrorCodes.BookingCannotBeCancelled, error.Code);
            Assert.Equal(AppErrorKind.Conflict, error.Kind);
        }

        Assert.Equal(status, (await ReadBookingAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task ClientListIncludesOwnBookingAndExcludesAnotherClientsBooking()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        await using var otherSeed = await BookingTestSetup.CreateAsync(fixture);
        var own = await InsertBookingAsync(seed);
        var other = await InsertBookingAsync(otherSeed);
        await using var scope = seed.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BookingService>();

        var bookings = await service.ListBookingsByClientAsync(new Actor(seed.Client.Id), default);

        Assert.Equal(own.Id, Assert.Single(bookings).Id);
        Assert.DoesNotContain(bookings, booking => booking.Id == other.Id);
    }

    [Fact]
    public async Task StaffListRequiresAssignedUserAndFiltersByDate()
    {
        await using var seed = await BookingTestSetup.CreateAsync(fixture);
        var booking = await InsertBookingAsync(seed);
        var secondBooking = await InsertBookingAsync(
            seed, startsAtUtc: booking.StartsAtUtc.AddHours(1));
        var staff = await GetStaffActorAsync(booking);
        var date = DateOnly.FromDateTime(booking.StartsAtUtc);
        await using var scope = seed.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BookingService>();

        var error = await Assert.ThrowsAsync<AppException>(() => service.ListByStaffAndRangeAsync(
            new Actor(seed.Client.Id), booking.StaffMemberId, date, date, default));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);

        var appointments = await service.ListByStaffAndRangeAsync(
            staff, booking.StaffMemberId, date, date, default);
        Assert.Equal(2, appointments.Count);
        Assert.Contains(appointments, appointment => appointment.Id == booking.Id);
        Assert.Contains(appointments, appointment => appointment.Id == secondBooking.Id);

        var nextDay = date.AddDays(1);
        Assert.Empty(await service.ListByStaffAndRangeAsync(
            staff, booking.StaffMemberId, nextDay, nextDay, default));
    }

    private async Task<Actor> GetStaffActorAsync(Booking booking)
    {
        await using var db = fixture.CreateContext();
        var staff = await db.StaffMembers.SingleAsync(member => member.Id == booking.StaffMemberId);
        return new Actor(staff.UserId);
    }

    private async Task<Booking> InsertBookingAsync(
        BookingTestSetup seed,
        BookingStatus status = BookingStatus.Confirmed,
        DateTime? startsAtUtc = null)
    {
        await using var db = fixture.CreateContext();
        var request = seed.Request with
        {
            RequestId = Guid.NewGuid(),
            StartAtUtc = startsAtUtc ?? seed.Request.StartAtUtc
        };
        var offering = await db.StaffServices.SingleAsync(x => x.Id == request.StaffServiceId);
        var booking = new Booking(seed.Client.Id, offering.StaffMemberId, offering.Id, request.RequestId,
            request.StartAtUtc, request.ExpectedPrice, request.ExpectedDurationMinutes,
            request.StartAtUtc.AddHours(-2));
        if (status == BookingStatus.Cancelled)
            booking.Cancel(booking.StartsAtUtc.AddSeconds(-1));
        if (status == BookingStatus.Completed)
            booking.Complete(booking.EndsAtUtc);

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private async Task<Booking> ReadBookingAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Bookings.SingleAsync(booking => booking.Id == id);
    }

    private static async Task<BookingDto?> TransitionAsync(
        BookingTestSetup seed, Booking booking, Actor actor, DateTime now, bool complete)
    {
        await using var host = seed.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IClock>(new FixedClock(now))));
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BookingService>();
        return complete
            ? await service.CompleteAsync(actor, booking.Id, default)
            : await service.CancelAsync(actor, booking.Id, default);
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }
}
