using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.Infrastructure.Catalog;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SharedServiceActivationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DeactivationIncludesInactiveLinksAndPreservesAllIndividualFlags()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Data.Staff.Id);
        var otherBefore = await Versions(setup.Data.Other.Id);
        var unrelatedBefore = await Versions(setup.UnrelatedStaffId);
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        var result = await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Data.Owner.Id), setup.Data.ActiveService.Id, false, default);
        Assert.False(result.IsActive);
        Assert.Equal(setup.Data.ActiveService.Id, result.Id);
        Assert.Equal(setup.Data.ActiveService.Name, result.Name);
        Assert.Equal(before with { StaffVersion = before.StaffVersion + 1 }, await Versions(setup.Data.Staff.Id));
        Assert.Equal(otherBefore with { StaffVersion = otherBefore.StaffVersion + 1 }, await Versions(setup.Data.Other.Id));
        Assert.Equal(unrelatedBefore, await Versions(setup.UnrelatedStaffId));
        await using var verify = fixture.CreateContext();
        var linked = await verify.StaffServices.Where(o => o.ServiceId == setup.Data.ActiveService.Id).ToListAsync();
        Assert.Equal(2, linked.Count);
        var first = linked.Single(o => o.Id == setup.Data.Active.Id);
        Assert.True(first.IsActive);
        Assert.Equal(12.50m, first.Price);
        Assert.Equal(30, first.DurationMinutes);
        Assert.False(linked.Single(o => o.StaffMemberId == setup.Data.Other.Id).IsActive);
        Assert.False((await verify.StaffMembers.SingleAsync(s => s.Id == setup.Data.Other.Id)).IsActive);
    }

    [Fact]
    public async Task ReactivationRestoresTheSharedFlagWithoutReactivatingDisabledOfferingsOrPublishingVersions()
    {
        await using var setup = await Seed();
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        var service = Service(scope.ServiceProvider);
        var actor = new Actor(setup.Data.Owner.Id);
        await service.SetActiveAsync(actor, setup.Data.ActiveService.Id, false, default);
        var before = await Versions(setup.Data.Staff.Id);
        var otherBefore = await Versions(setup.Data.Other.Id);
        var result = await service.SetActiveAsync(actor, setup.Data.ActiveService.Id, true, default);
        Assert.True(result.IsActive);
        Assert.Equal(before, await Versions(setup.Data.Staff.Id));
        Assert.Equal(otherBefore, await Versions(setup.Data.Other.Id));
        var catalogue = scope.ServiceProvider.GetRequiredService<ICatalogRepository>();
        Assert.Equal(setup.Data.Active.Id, Assert.Single(await catalogue.ListActiveOfferingsAsync(setup.Data.Staff.Id, default)).Id);
        Assert.Empty(await catalogue.ListActiveOfferingsAsync(setup.Data.Other.Id, default));
        await using var verify = fixture.CreateContext();
        Assert.False((await verify.StaffServices.SingleAsync(o => o.StaffMemberId == setup.Data.Other.Id)).IsActive);
    }

    [Fact]
    public async Task AlreadyInactiveSharedServiceIsANoOp()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Data.Staff.Id);
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        for (var i = 0; i < 2; i++)
            Assert.False((await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Data.Owner.Id), setup.Data.InactiveService.Id, false, default)).IsActive);
        Assert.Equal(before, await Versions(setup.Data.Staff.Id));
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.StaffServices.SingleAsync(o => o.Id == setup.Data.Hidden.Id)).IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyOwnerCanChangeSharedServiceStatus(bool employee)
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Data.Staff.Id);
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).SetActiveAsync(
            new Actor(employee ? setup.Data.Employee.Id : setup.Data.Customer.Id), setup.Data.ActiveService.Id, false, default));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Equal(before, await Versions(setup.Data.Staff.Id));
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.Data.ActiveService.Id)).IsActive);
    }

    [Fact]
    public async Task MissingServicesDoNotPublishInvalidation()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Data.Staff.Id);
        foreach (var id in new[] { Guid.Empty, Guid.NewGuid() })
        {
            await using var scope = setup.Data.Host.Services.CreateAsyncScope();
            var error = await Assert.ThrowsAsync<AppException>(() => Service(scope.ServiceProvider).SetActiveAsync(
                new Actor(setup.Data.Owner.Id), id, false, default));
            Assert.Equal(ErrorCodes.ServiceNotFound, error.Code);
            Assert.Equal(AppErrorKind.NotFound, error.Kind);
        }
        Assert.Equal(before, await Versions(setup.Data.Staff.Id));
    }

    [Fact]
    public async Task ServiceWithoutOfferingsCanChangeStatusWithoutChangingStaffVersions()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Data.Staff.Id);
        var empty = new Service("Unassigned " + Guid.NewGuid());
        await using (var db = fixture.CreateContext()) { db.Services.Add(empty); await db.SaveChangesAsync(); }
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        var service = Service(scope.ServiceProvider);
        Assert.False((await service.SetActiveAsync(new Actor(setup.Data.Owner.Id), empty.Id, false, default)).IsActive);
        Assert.True((await service.SetActiveAsync(new Actor(setup.Data.Owner.Id), empty.Id, true, default)).IsActive);
        Assert.Equal(before, await Versions(setup.Data.Staff.Id));
    }

    [Fact]
    public async Task TrackedServiceIsReloadedBeforeDecidingWhetherTheRequestIsANoOp()
    {
        await using var setup = await Seed();
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        var cached = await scope.ServiceProvider.GetRequiredService<ICatalogRepository>().GetServiceAsync(setup.Data.ActiveService.Id, default);
        Assert.True(cached!.IsActive);
        await using (var db = fixture.CreateContext())
        {
            (await db.Services.SingleAsync(s => s.Id == setup.Data.ActiveService.Id)).Deactivate();
            await db.SaveChangesAsync();
        }
        var before = await Versions(setup.Data.Staff.Id);
        var result = await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Data.Owner.Id), setup.Data.ActiveService.Id, true, default);
        Assert.True(result.IsActive);
        Assert.Equal(before, await Versions(setup.Data.Staff.Id));
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.Data.ActiveService.Id)).IsActive);
    }

    [Fact]
    public async Task FailureRollsBackSharedStatusButPreservesPublishedStaffVersions()
    {
        await using var setup = await Seed();
        var before = await Versions(setup.Data.Staff.Id);
        var otherBefore = await Versions(setup.Data.Other.Id);
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Assert.ThrowsAsync<IOException>(() => Service(scope.ServiceProvider, new FailingSave(db)).SetActiveAsync(
            new Actor(setup.Data.Owner.Id), setup.Data.ActiveService.Id, false, default));
        Assert.Equal(before with { StaffVersion = before.StaffVersion + 1 }, await Versions(setup.Data.Staff.Id));
        Assert.Equal(otherBefore with { StaffVersion = otherBefore.StaffVersion + 1 }, await Versions(setup.Data.Other.Id));
        await using var verify = fixture.CreateContext();
        Assert.True((await verify.Services.SingleAsync(s => s.Id == setup.Data.ActiveService.Id)).IsActive);
    }

    [Fact]
    public async Task SharedServiceDeactivationPreservesAcceptedBookingReferencesAndTerms()
    {
        await using var setup = await Seed();
        var now = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking(setup.Data.Customer.Id, setup.Data.Staff.Id, setup.Data.Active.Id,
            Guid.NewGuid(), now.AddDays(1), 12.50m, 30, now);
        await using (var db = fixture.CreateContext()) { db.Bookings.Add(booking); await db.SaveChangesAsync(); }
        await using var scope = setup.Data.Host.Services.CreateAsyncScope();
        await Service(scope.ServiceProvider).SetActiveAsync(new Actor(setup.Data.Owner.Id), setup.Data.ActiveService.Id, false, default);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Bookings.SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(setup.Data.Active.Id, saved.StaffServiceId);
        Assert.Equal(booking.Status, saved.Status);
        Assert.Equal(12.50m, saved.Price);
        Assert.Equal(30, saved.DurationMinutes);
        Assert.Equal(now.AddDays(1), saved.StartsAtUtc);
        Assert.Equal(now.AddDays(1).AddMinutes(30), saved.EndsAtUtc);
    }

    [Fact]
    public async Task NewOfferingCannotEscapeTheSharedServiceMembershipGateDuringDeactivation()
    {
        await using var setup = await Seed();
        var gate = new MembershipGate();
        await using var app = setup.Data.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<ICatalogServiceLock>(provider => new ObservedServiceGate(
                provider.GetRequiredService<AppDbContext>(), gate))));
        await using var owner = app.Services.CreateAsyncScope();
        await using var creator = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var deactivation = Service(owner.ServiceProvider).SetActiveAsync(new Actor(setup.Data.Owner.Id), setup.Data.ActiveService.Id, false, ct);
        Task<StaffServiceDto>? creation = null;
        try
        {
            await Task.WhenAny(deactivation, gate.Held.Task).WaitAsync(ct);
            if (deactivation.IsCompleted) await deactivation;
            var holderPid = await gate.Held.Task.WaitAsync(ct);
            creation = creator.ServiceProvider.GetRequiredService<OfferingTermsService>().CreateAsync(
                new Actor(setup.Data.Owner.Id), setup.UnrelatedStaffId, setup.Data.ActiveService.Id, new(40m, 45), ct);
            await Task.WhenAny(creation, gate.Waiter.Task).WaitAsync(ct);
            Assert.False(creation.IsCompleted, "Creation must first wait for the shared-service gate.");
            var waiterPid = await gate.Waiter.Task.WaitAsync(ct);
            await using var observer = new NpgsqlConnection(fixture.ConnectionString);
            await observer.OpenAsync(ct);
            while (true)
            {
                Assert.False(creation.IsCompleted);
                await using var query = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", observer);
                query.Parameters.AddWithValue("holder", holderPid);
                query.Parameters.AddWithValue("waiter", waiterPid);
                if ((bool)(await query.ExecuteScalarAsync(ct))!) break;
                await Task.Delay(10, ct);
            }
        }
        catch
        {
            await timeout.CancelAsync();
            gate.Release.TrySetResult();
            try { await Task.WhenAll(deactivation, creation ?? (Task)Task.CompletedTask); } catch { }
            throw;
        }
        finally { gate.Release.TrySetResult(); }
        Assert.False((await deactivation).IsActive);
        var rejected = await Assert.ThrowsAsync<AppException>(() => creation!);
        Assert.Equal(ErrorCodes.ServiceInactive, rejected.Code);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.StaffServices.AnyAsync(o => o.StaffMemberId == setup.UnrelatedStaffId && o.ServiceId == setup.Data.ActiveService.Id, ct));
        Assert.Equal(2, await verify.StaffServices.CountAsync(o => o.ServiceId == setup.Data.ActiveService.Id, ct));
    }

    private sealed class MembershipGate
    {
        public int Attempts;
        public TaskCompletionSource<int> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Waiter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObservedServiceGate(AppDbContext db, MembershipGate gate) : ICatalogServiceLock
    {
        public async Task AcquireAsync(Guid serviceId, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref gate.Attempts);
            await db.Database.OpenConnectionAsync(ct);
            var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
            if (attempt == 2) gate.Waiter.TrySetResult(pid);
            await new PostgresCatalogServiceLock(db).AcquireAsync(serviceId, ct);
            if (attempt == 1)
            {
                gate.Held.TrySetResult(pid);
                await gate.Release.Task.WaitAsync(ct);
            }
        }
    }

    private static SharedServiceActivationService Service(IServiceProvider provider, IUnitOfWork? unit = null) => new(
        provider.GetRequiredService<ICatalogRepository>(), provider.GetRequiredService<IAccessService>(),
        unit ?? provider.GetRequiredService<IUnitOfWork>(), provider.GetRequiredService<IStaffOperationLock>(),
        provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ICatalogServiceLock>());

    private async Task<Setup> Seed()
    {
        var data = await OfferingOverviewScenario.Create(fixture);
        await using var db = fixture.CreateContext();
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null) db.Add(settings = new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == settings.Id)) db.Add(new BookingCoordination(settings));
        var user = new User(Random.Shared.NextInt64(10_000_000_000, 80_000_000_000), "Unrelated", null, DateTime.UtcNow);
        var unrelated = new StaffMember(user.Id, "Unrelated");
        var otherOffering = new StaffService(data.Other.Id, data.ActiveService.Id, 99m, 90);
        otherOffering.Deactivate();
        (await db.StaffMembers.SingleAsync(s => s.Id == data.Other.Id)).Deactivate();
        db.AddRange(user, unrelated, otherOffering, new BookingCoordination(data.Staff),
            new BookingCoordination(data.Other), new BookingCoordination(unrelated));
        await db.SaveChangesAsync();
        return new(data, unrelated.Id);
    }

    private async Task<BookingCoordinationVersions> Versions(Guid staffId)
    {
        await using var db = fixture.CreateContext();
        return await new BookingCoordinationRepository(db).ReadVersionsAsync(staffId, default);
    }
    private sealed record Setup(OfferingOverviewScenario Data, Guid UnrelatedStaffId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Data.DisposeAsync();
    }
    private sealed class FailingSave(AppDbContext db) : IUnitOfWork
    {
        public Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct) => db.BeginTransactionAsync(ct);
        public Task<int> SaveChangesAsync(CancellationToken ct) => throw new IOException("Injected save failure");
    }
}
