using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AppointmentBooking.IntegrationTests;

public sealed class MigrationUpgradeTests
{
    private const string InitialMigration = "20261102120326_InitialCreate";
    private const string LatestMigration = "20261107171452_AddBookingCoordination";
    private static readonly string[] OriginalTables =
        ["business_settings", "users", "staff_members", "services", "staff_services", "working_hours", "staff_days_off", "bookings"];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpgradePreservesOriginalRowsAndBackfillsCoordination(bool populated, bool settingsExist)
    {
        // Start at the old schema directly; never downgrade a shared or developer database.
        await using var container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("appointmentbooking_upgrade_tests")
            .WithUsername("appointmentbooking_tests")
            .WithPassword("appointmentbooking_tests")
            .Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options;
        await using (var oldDb = new AppDbContext(options))
        {
            await oldDb.GetService<IMigrator>().MigrateAsync(InitialMigration);
            Assert.Equal(new[] { InitialMigration }, await oldDb.Database.GetAppliedMigrationsAsync());
            Assert.Equal(new[] { LatestMigration }, await oldDb.Database.GetPendingMigrationsAsync());
        }

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var absent = new NpgsqlCommand("SELECT to_regclass('public.booking_coordination') IS NULL", connection))
            Assert.Equal(true, await absent.ExecuteScalarAsync());
        if (populated)
        {
            await using var seed = new NpgsqlCommand(SeedSql, connection);
            await seed.ExecuteNonQueryAsync();
        }
        if (settingsExist)
        {
            await using var settings = new NpgsqlCommand("INSERT INTO business_settings VALUES (1, 'Owner-edited business', 'Specialist', 'Asia/Yerevan', 'USD')", connection);
            await settings.ExecuteNonQueryAsync();
        }
        var before = await ReadOriginalRowsAsync(connection);

        await using var upgraded = new AppDbContext(options);
        await upgraded.Database.MigrateAsync();
        Assert.Equal(new[] { InitialMigration, LatestMigration }, await upgraded.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
        Assert.False(upgraded.Database.HasPendingModelChanges());
        Assert.Equal(before, await ReadOriginalRowsAsync(connection));

        var rows = await upgraded.BookingCoordinationRows.AsNoTracking().ToListAsync();
        Assert.Equal((populated ? 2 : 0) + (settingsExist ? 1 : 0), rows.Count);
        Assert.All(rows, row => Assert.Equal(0L, row.ChangeVersion));
        Assert.All(rows, row => Assert.True(row.BusinessSettingsId.HasValue ^ row.StaffMemberId.HasValue));
        var expectedStaff = await upgraded.StaffMembers.OrderBy(staff => staff.Id).Select(staff => staff.Id).ToListAsync();
        Assert.Equal(expectedStaff, rows.Where(row => row.StaffMemberId.HasValue)
            .Select(row => row.StaffMemberId!.Value).OrderBy(id => id).ToList());
        Assert.Equal(settingsExist ? new[] { 1 } : Array.Empty<int>(), rows
            .Where(row => row.BusinessSettingsId.HasValue).Select(row => row.BusinessSettingsId!.Value).ToArray());

        // Reapplying the upgrade must preserve existing versions and avoid duplicate backfill.
        await upgraded.BookingCoordinationRows.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChangeVersion, 7L));
        await upgraded.Database.MigrateAsync();
        var repeated = await upgraded.BookingCoordinationRows.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(rows.Select(row => row.Id).OrderBy(id => id), repeated.Select(row => row.Id));
        Assert.All(repeated, row => Assert.Equal(7L, row.ChangeVersion));
        Assert.Equal(before, await ReadOriginalRowsAsync(connection));

        // Generated IDs must continue beyond migration backfill IDs for later staff creation.
        var user = new User(99, "New employee", null, DateTime.UtcNow);
        var staff = new StaffMember(user.Id, "New employee");
        var coordination = new BookingCoordination(staff);
        upgraded.Users.Add(user);
        upgraded.StaffMembers.Add(staff);
        upgraded.BookingCoordinationRows.Add(coordination);
        await upgraded.SaveChangesAsync();
        Assert.True(coordination.Id > rows.Select(row => row.Id).DefaultIfEmpty(0L).Max());
        Assert.Equal(0L, coordination.ChangeVersion);
        Assert.Equal(staff.Id, coordination.StaffMemberId);
    }

    private static async Task<string[]> ReadOriginalRowsAsync(NpgsqlConnection connection)
    {
        var result = new List<string>();
        foreach (var table in OriginalTables)
        {
            // Table names come only from the fixed list above. JSON captures every column,
            // including IDs, activation, accepted booking terms and lifecycle timestamps.
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY id), '[]'::jsonb)::text FROM {table} t", connection);
            result.Add((string)(await command.ExecuteScalarAsync())!);
        }
        return result.ToArray();
    }

    private const string SeedSql = """
        INSERT INTO users VALUES
        ('00000000-0000-0000-0000-000000000001', 1, 'Employee', NULL, '2029-01-01T00:00:00Z'),
        ('00000000-0000-0000-0000-000000000002', 2, 'Client', NULL, '2029-01-01T00:00:00Z');
        INSERT INTO staff_members VALUES ('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001', 'Employee', true);
        INSERT INTO services VALUES ('00000000-0000-0000-0000-000000000004', 'Consultation', true);
        INSERT INTO staff_services VALUES ('00000000-0000-0000-0000-000000000005', '00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000004', 25, 30, true);
        INSERT INTO working_hours VALUES ('00000000-0000-0000-0000-000000000007', '00000000-0000-0000-0000-000000000003', '09:00', '18:00', NULL, NULL);
        INSERT INTO staff_days_off VALUES
        ('00000000-0000-0000-0000-000000000009', '00000000-0000-0000-0000-000000000003', 0, NULL),
        ('00000000-0000-0000-0000-000000000011', '00000000-0000-0000-0000-000000000003', NULL, '2030-01-02');
        INSERT INTO bookings VALUES ('00000000-0000-0000-0000-000000000012', '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000005', '00000000-0000-0000-0000-000000000013', '2030-01-01T10:00:00Z', '2030-01-01T10:30:00Z', 25, 30, 'Confirmed', '2029-01-01T00:00:00Z', NULL, NULL);
        INSERT INTO users VALUES ('00000000-0000-0000-0000-000000000020', 3, 'Inactive employee', 'inactive_user', '2029-01-01T00:00:00Z');
        INSERT INTO staff_members VALUES ('00000000-0000-0000-0000-000000000021', '00000000-0000-0000-0000-000000000020', 'Inactive employee', false);
        INSERT INTO bookings
        SELECT '00000000-0000-0000-0000-000000000014', client_user_id, staff_member_id,
            staff_service_id, '00000000-0000-0000-0000-000000000015', starts_at_utc,
            ends_at_utc, price, duration_minutes, 'Cancelled', created_at_utc,
            starts_at_utc - INTERVAL '1 hour', NULL FROM bookings;
        INSERT INTO bookings
        SELECT '00000000-0000-0000-0000-000000000016', client_user_id, staff_member_id,
            staff_service_id, '00000000-0000-0000-0000-000000000017', starts_at_utc,
            ends_at_utc, price, duration_minutes, 'Completed', created_at_utc, NULL,
            ends_at_utc FROM bookings WHERE status = 'Confirmed';
        UPDATE staff_services SET price = 50, duration_minutes = 60, is_active = false;
        UPDATE services SET is_active = false;
        """;
}
