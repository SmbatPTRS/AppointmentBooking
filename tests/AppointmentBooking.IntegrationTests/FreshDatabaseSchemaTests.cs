using AppointmentBooking.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppointmentBooking.IntegrationTests;

// A dedicated container keeps migration/schema acceptance separate from feature fixtures.
public sealed class FreshDatabaseSchemaTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public FreshDatabaseSchemaTests(PostgresFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task FreshDatabaseHasAllMigrationsAndCurrentModel()
    {
        await using var db = fixture.CreateContext();
        Assert.Equal(new[] { "20261102120326_InitialCreate", "20261107171452_AddBookingCoordination" },
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tables = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename", connection);
        var actual = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
            while (await reader.ReadAsync()) actual.Add(reader.GetString(0));
        Assert.Equal(new[] { "booking_coordination", "bookings", "business_settings", "services", "staff_days_off",
            "staff_members", "staff_services", "users", "working_hours" }, actual);

        await using var extension = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'", connection);
        Assert.Equal(1L, await extension.ExecuteScalarAsync());
        await using var foreignKeys = new NpgsqlCommand("""
            SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = 'public' AND c.contype = 'f' AND c.confdeltype <> 'r'
            """, connection);
        Assert.Equal(0L, await foreignKeys.ExecuteScalarAsync());
        // Reapplying migrations is harmless and must not try to recreate existing tables.
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task AdjacentBookingsAndCompletedHistoryAreAllowed()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(SeedSql + """

            INSERT INTO bookings
            SELECT '00000000-0000-0000-0000-000000000014', client_user_id, staff_member_id,
                staff_service_id, '00000000-0000-0000-0000-000000000015', ends_at_utc,
                ends_at_utc + INTERVAL '30 minutes', price, duration_minutes, status,
                created_at_utc, NULL, NULL FROM bookings;
            UPDATE bookings SET status = 'Completed', completed_at_utc = ends_at_utc
            WHERE id = '00000000-0000-0000-0000-000000000012';
            INSERT INTO bookings
            SELECT '00000000-0000-0000-0000-000000000016', client_user_id, staff_member_id,
                staff_service_id, '00000000-0000-0000-0000-000000000017', starts_at_utc,
                ends_at_utc, price, duration_minutes, 'Confirmed', created_at_utc, NULL, NULL
            FROM bookings WHERE status = 'Completed';
            """, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM bookings", connection, transaction);
        Assert.Equal(3L, await count.ExecuteScalarAsync());
    }

    public static IEnumerable<object[]> InvalidWrites()
    {
        yield return Case("UPDATE users SET telegram_user_id = 1 WHERE telegram_user_id = 2", "23505", "ux_users_telegram_user_id");
        yield return Case("INSERT INTO staff_members VALUES ('00000000-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000001', 'Duplicate', true)", "23505", "ux_staff_members_user_id");
        yield return Case("UPDATE business_settings SET id = 2", "23514", "ck_business_settings_singleton");
        yield return Case("INSERT INTO business_settings VALUES (1, 'Duplicate', 'Employee', 'UTC', 'AMD')", "23505", "PK_business_settings");
        yield return Case("UPDATE staff_services SET price = -1", "23514", "ck_staff_services_price");
        yield return Case("UPDATE staff_services SET duration_minutes = 0", "23514", "ck_staff_services_duration_minutes");
        yield return Case("INSERT INTO staff_services SELECT '00000000-0000-0000-0000-000000000006', staff_member_id, service_id, price, duration_minutes, is_active FROM staff_services", "23505", "ux_staff_services_staff_member_id_service_id");
        yield return Case("UPDATE working_hours SET opens_at = closes_at", "23514", "ck_working_hours_interval");
        yield return Case("UPDATE working_hours SET break_starts_at = '12:00'", "23514", "ck_working_hours_break");
        yield return Case("UPDATE working_hours SET break_starts_at = '08:00', break_ends_at = '10:00'", "23514", "ck_working_hours_break");
        yield return Case("INSERT INTO working_hours SELECT '00000000-0000-0000-0000-000000000008', staff_member_id, opens_at, closes_at, NULL, NULL FROM working_hours", "23505", "ux_working_hours_staff_member_id");
        yield return Case("UPDATE staff_days_off SET weekday = NULL WHERE weekday IS NOT NULL", "23514", "ck_staff_days_off_one_kind");
        yield return Case("UPDATE staff_days_off SET date = '2030-01-01' WHERE weekday IS NOT NULL", "23514", "ck_staff_days_off_one_kind");
        yield return Case("UPDATE staff_days_off SET weekday = 7 WHERE weekday IS NOT NULL", "23514", "ck_staff_days_off_weekday");
        yield return Case("INSERT INTO staff_days_off SELECT '00000000-0000-0000-0000-000000000010', staff_member_id, weekday, NULL FROM staff_days_off WHERE weekday IS NOT NULL", "23505", "ux_staff_days_off_staff_weekday");
        yield return Case("INSERT INTO staff_days_off SELECT '00000000-0000-0000-0000-000000000010', staff_member_id, NULL, date FROM staff_days_off WHERE date IS NOT NULL", "23505", "ux_staff_days_off_staff_date");
        yield return Case("UPDATE bookings SET price = -1", "23514", "ck_bookings_price");
        yield return Case("UPDATE bookings SET duration_minutes = 31", "23514", "ck_bookings_snapshot_duration");
        yield return Case("UPDATE bookings SET status = 'Cancelled'", "23514", "ck_bookings_lifecycle");
        yield return Case("UPDATE bookings SET staff_member_id = '00000000-0000-0000-0000-000000000004'", "23503", "FK_bookings_staff_services_staff_service_id_staff_member_id", true);
        yield return Case("DELETE FROM staff_services", "23503", "FK_bookings_staff_services_staff_service_id_staff_member_id");
        yield return Case("INSERT INTO booking_coordination DEFAULT VALUES", "23514", "ck_booking_coordination_one_scope");
        yield return Case("INSERT INTO booking_coordination (business_settings_id, staff_member_id) VALUES (1, '00000000-0000-0000-0000-000000000003')", "23514", "ck_booking_coordination_one_scope");
        yield return Case("UPDATE booking_coordination SET change_version = -1", "23514", "ck_booking_coordination_version");
        yield return Case("INSERT INTO booking_coordination (business_settings_id) VALUES (1)", "23505", "IX_booking_coordination_business_settings_id");
        yield return Case("INSERT INTO booking_coordination (staff_member_id) VALUES ('00000000-0000-0000-0000-000000000003')", "23505", "IX_booking_coordination_staff_member_id");
    }

    private static object[] Case(string sql, string state, string constraint, bool secondStaff = false) =>
        new object[] { sql, state, constraint, secondStaff };

    [Theory]
    [MemberData(nameof(InvalidWrites))]
    public async Task DatabaseRejectsInvalidDirectWrites(string sql, string state, string constraint, bool secondStaff)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var seed = new NpgsqlCommand(SeedSql, connection, transaction);
        await seed.ExecuteNonQueryAsync();
        if (secondStaff)
        {
            await using var extra = new NpgsqlCommand("INSERT INTO staff_members VALUES ('00000000-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000002', 'Other employee', true)", connection, transaction);
            await extra.ExecuteNonQueryAsync();
        }
        await using var invalid = new NpgsqlCommand(sql, connection, transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
        Assert.Equal(state, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        // Invalid SQL aborts the transaction; dispose rolls back seed and attempted mutation.
    }

    private const string SeedSql = """
        INSERT INTO business_settings VALUES (1, 'Business', 'Employee', 'UTC', 'AMD');
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
        INSERT INTO booking_coordination (business_settings_id) VALUES (1);
        INSERT INTO booking_coordination (staff_member_id) VALUES ('00000000-0000-0000-0000-000000000003');
        """;
}
