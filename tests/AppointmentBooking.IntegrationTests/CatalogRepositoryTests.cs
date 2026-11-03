using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Repositories;
using AppointmentBooking.IntegrationTests.Fixtures;

namespace AppointmentBooking.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task BrowseUsesEmployeeTermsAndHidesDeactivatedRecords()
    {
        await using var db = fixture.CreateContext();
        var repo = new CatalogRepository(db);
        var first = new User(4300000001, "First", null, DateTime.UtcNow);
        var second = new User(4300000002, "Second", null, DateTime.UtcNow);
        var aram = new StaffMember(first.Id, "Aram");
        var david = new StaffMember(second.Id, "David");
        db.Users.AddRange(first, second);
        db.StaffMembers.AddRange(aram, david);
        var haircut = new Service("Haircut");
        var a = new StaffService(aram.Id, haircut.Id, 5000m, 40);
        var b = new StaffService(david.Id, haircut.Id, 6000m, 30);
        repo.Add(haircut);
        repo.Add(a);
        repo.Add(b);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var ct = CancellationToken.None;

        var aramResult = Assert.Single(await repo.ListActiveOfferingsAsync(aram.Id, ct));
        Assert.Equal(new StaffServiceDto(a.Id, aram.Id, haircut.Id, "Haircut", 5000m, 40, true), aramResult);
        var davidResult = Assert.Single(await repo.ListActiveOfferingsAsync(david.Id, ct));
        Assert.Equal(new StaffServiceDto(b.Id, david.Id, haircut.Id, "Haircut", 6000m, 30, true), davidResult);

        var tracked = Assert.IsType<StaffService>(await repo.GetOfferingAsync(aram.Id, haircut.Id, ct));
        tracked.Deactivate();
        await db.SaveChangesAsync();
        Assert.Empty(await repo.ListActiveOfferingsAsync(aram.Id, ct));
        Assert.Single(await repo.ListActiveOfferingsAsync(david.Id, ct));

        var staff = await db.StaffMembers.FindAsync(david.Id);
        staff!.Deactivate();
        await db.SaveChangesAsync();
        Assert.Empty(await repo.ListActiveOfferingsAsync(david.Id, ct));

        staff.Activate();
        var service = Assert.IsType<Service>(await repo.GetServiceAsync(haircut.Id, ct));
        service.Deactivate();
        await db.SaveChangesAsync();
        Assert.Empty(await repo.ListActiveOfferingsAsync(david.Id, ct));
        Assert.DoesNotContain(await repo.ListActiveServicesAsync(ct), s => s.Id == service.Id);
        Assert.NotNull(await repo.GetOfferingAsync(david.Id, service.Id, ct));
    }
}
