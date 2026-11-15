using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

        var originalCoordinationId = await db.BookingCoordinationRows.Select(row => row.Id).SingleAsync();
        settings.Update("Owner edit", "Specialist", "Asia/Yerevan", "USD");
        await db.SaveChangesAsync();
        await db.BookingCoordinationRows.ExecuteUpdateAsync(
            setters => setters.SetProperty(row => row.ChangeVersion, 3L));

        await seeder.SeedBusinessSettingsAsync(defaults);

        var persisted = await db.BusinessSettings.AsNoTracking().SingleAsync();
        Assert.Equal(BusinessSettings.SingletonId, persisted.Id);
        Assert.Equal("Owner edit", persisted.DisplayName);
        Assert.Equal("Specialist", persisted.StaffLabel);
        Assert.Equal("Asia/Yerevan", persisted.TimeZoneId);
        Assert.Equal("USD", persisted.CurrencyCode);
        var coordination = await db.BookingCoordinationRows.AsNoTracking().SingleAsync();
        Assert.Equal(BusinessSettings.SingletonId, coordination.BusinessSettingsId);
        Assert.Equal(originalCoordinationId, coordination.Id);
        Assert.Equal(3L, coordination.ChangeVersion);
    }

    [Fact]
    public async Task FreshSeedAndRerunsAcrossContextsKeepExactlyOnePair()
    {
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        long originalId;
        await using (var first = isolated.CreateContext())
        {
            await new DatabaseSeeder(first).SeedBusinessSettingsAsync(
                new BusinessSettings(" Business ", " Employee ", " UTC ", "amd"));
            originalId = (await first.BookingCoordinationRows.SingleAsync()).Id;
        }
        // A later invocation may supply different setup defaults; existing data wins.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var rerun = isolated.CreateContext();
            await new DatabaseSeeder(rerun).SeedBusinessSettingsAsync(
                new BusinessSettings("Different business", "Specialist", "Asia/Yerevan", "USD"));
        }
        await using var verify = isolated.CreateContext();
        var settings = await verify.BusinessSettings.SingleAsync();
        Assert.Equal(BusinessSettings.SingletonId, settings.Id);
        Assert.Equal("Business", settings.DisplayName);
        Assert.Equal("Employee", settings.StaffLabel);
        Assert.Equal("UTC", settings.TimeZoneId);
        Assert.Equal("AMD", settings.CurrencyCode);
        var row = await verify.BookingCoordinationRows.SingleAsync();
        Assert.Equal(originalId, row.Id);
        Assert.Equal(BusinessSettings.SingletonId, row.BusinessSettingsId);
        Assert.Null(row.StaffMemberId);
        Assert.Equal(0L, row.ChangeVersion);
    }

    [Fact]
    public async Task ExistingSettingsGainMissingCoordinationWithoutChangingStaffVersions()
    {
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        long staffCoordinationId;
        await using (var setup = isolated.CreateContext())
        {
            setup.BusinessSettings.Add(new BusinessSettings("Owner business", "Specialist", "Asia/Yerevan", "USD"));
            var user = new User(41, "Employee", null, DateTime.UtcNow);
            var staff = new StaffMember(user.Id, "Employee");
            var row = new BookingCoordination(staff);
            setup.Users.Add(user);
            setup.StaffMembers.Add(staff);
            setup.BookingCoordinationRows.Add(row);
            await setup.SaveChangesAsync();
            staffCoordinationId = row.Id;
            await setup.BookingCoordinationRows.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ChangeVersion, 9L));
        }
        await using (var seed = isolated.CreateContext())
        {
            var seeder = new DatabaseSeeder(seed);
            await seeder.SeedBusinessSettingsAsync(new BusinessSettings("Defaults", "Employee", "UTC", "AMD"));
            await seeder.SeedBusinessSettingsAsync(new BusinessSettings("Defaults", "Employee", "UTC", "AMD"));
        }
        await using var verify = isolated.CreateContext();
        var settings = await verify.BusinessSettings.SingleAsync();
        Assert.Equal("Owner business", settings.DisplayName);
        Assert.Equal("Specialist", settings.StaffLabel);
        Assert.Equal("Asia/Yerevan", settings.TimeZoneId);
        Assert.Equal("USD", settings.CurrencyCode);
        Assert.Equal(2, await verify.BookingCoordinationRows.CountAsync());
        var business = await verify.BookingCoordinationRows.SingleAsync(row => row.BusinessSettingsId == 1);
        Assert.Equal(0L, business.ChangeVersion);
        Assert.Null(business.StaffMemberId);
        var staffRow = await verify.BookingCoordinationRows.SingleAsync(row => row.Id == staffCoordinationId);
        Assert.NotNull(staffRow.StaffMemberId);
        Assert.Null(staffRow.BusinessSettingsId);
        Assert.Equal(9L, staffRow.ChangeVersion);
    }

    [Fact]
    public async Task ConcurrentSeedsOnSeparateContextsCreateOneConsistentPair()
    {
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        await using var first = isolated.CreateContext();
        await using var second = isolated.CreateContext();
        var defaults = new BusinessSettings("Business", "Employee", "UTC", "AMD");
        await Task.WhenAll(
            new DatabaseSeeder(first).SeedBusinessSettingsAsync(defaults),
            new DatabaseSeeder(second).SeedBusinessSettingsAsync(defaults));

        await using var verify = isolated.CreateContext();
        var settings = await verify.BusinessSettings.SingleAsync();
        Assert.Equal("Business", settings.DisplayName);
        var row = await verify.BookingCoordinationRows.SingleAsync();
        Assert.Equal(settings.Id, row.BusinessSettingsId);
        Assert.Null(row.StaffMemberId);
        Assert.Equal(0L, row.ChangeVersion);
    }

    [Fact]
    public async Task CoordinationFailureRollsBackSettingsAndAllowsRetry()
    {
        await using var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        await using var db = isolated.CreateContext();
        // Force the second write to fail after the settings INSERT has succeeded.
        // The trigger exists only in this test container.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_seed_coordination() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Test-only coordination failure';
            END;
            $$;
            CREATE TRIGGER reject_seed_coordination BEFORE INSERT ON booking_coordination
                FOR EACH ROW EXECUTE FUNCTION reject_seed_coordination();
            """);
        var defaults = new BusinessSettings("Business", "Employee", "UTC", "AMD");
        var seeder = new DatabaseSeeder(db);
        var error = await Assert.ThrowsAsync<PostgresException>(() => seeder.SeedBusinessSettingsAsync(defaults));
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Null(db.Database.CurrentTransaction);
        await using (var verify = isolated.CreateContext())
        {
            Assert.Empty(await verify.BusinessSettings.ToListAsync());
            Assert.Empty(await verify.BookingCoordinationRows.ToListAsync());
        }
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_seed_coordination ON booking_coordination; DROP FUNCTION reject_seed_coordination()");
        await seeder.SeedBusinessSettingsAsync(defaults);
        await using var recovered = isolated.CreateContext();
        Assert.Equal("Business", (await recovered.BusinessSettings.SingleAsync()).DisplayName);
        Assert.Equal(BusinessSettings.SingletonId, (await recovered.BookingCoordinationRows.SingleAsync()).BusinessSettingsId);
    }
}
