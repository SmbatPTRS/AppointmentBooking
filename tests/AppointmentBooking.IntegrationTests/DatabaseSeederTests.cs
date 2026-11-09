using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests;

public sealed class DatabaseSeederTests
{
    [Fact]
    public async Task RepeatedSeedPreservesEditedSettingsAndCoordinationVersion()
    {
        // Keep the singleton in a separate database from the other PostgreSQL tests.
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        await using var db = isolated.CreateContext();
        var defaults = new BusinessSettings("Business", "Employee", "UTC", "AMD");
        var seeder = new DatabaseSeeder(db);

        await seeder.SeedBusinessSettingsAsync(defaults);
        var settings = await db.BusinessSettings.SingleAsync();
        Assert.Equal("Business", settings.DisplayName);
        Assert.Equal(0L, await db.BookingCoordinationRows.Select(row => row.ChangeVersion).SingleAsync());

        settings.Update("Owner edit", "Specialist", "UTC", "USD");
        await db.SaveChangesAsync();
        await db.BookingCoordinationRows.ExecuteUpdateAsync(
            setters => setters.SetProperty(row => row.ChangeVersion, 3L));

        await seeder.SeedBusinessSettingsAsync(defaults);

        var persisted = await db.BusinessSettings.AsNoTracking().SingleAsync();
        Assert.Equal("Owner edit", persisted.DisplayName);
        Assert.Equal("USD", persisted.CurrencyCode);
        var coordination = await db.BookingCoordinationRows.AsNoTracking().SingleAsync();
        Assert.Equal(BusinessSettings.SingletonId, coordination.BusinessSettingsId);
        Assert.Equal(3L, coordination.ChangeVersion);
    }
}
