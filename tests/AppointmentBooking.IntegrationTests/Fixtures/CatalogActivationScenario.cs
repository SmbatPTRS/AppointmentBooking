using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.IntegrationTests.Fixtures;

public static class CatalogActivationScenario
{
    public static async Task<OfferingOverviewScenario> Create(PostgresFixture fixture)
    {
        var setup = await OfferingOverviewScenario.Create(fixture);
        await using var db = fixture.CreateContext();
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        if (settings is null) db.Add(settings = new BusinessSettings("Studio", "Staff", "UTC", "AMD"));
        if (!await db.BookingCoordinationRows.AnyAsync(r => r.BusinessSettingsId == settings.Id)) db.Add(new BookingCoordination(settings));
        db.AddRange(new BookingCoordination(setup.Staff), new BookingCoordination(setup.Other));
        await db.SaveChangesAsync();
        return setup;
    }
}
