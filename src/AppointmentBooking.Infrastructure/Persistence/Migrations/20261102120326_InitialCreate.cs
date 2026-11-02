using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppointmentBooking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    staff_label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_settings", x => x.id);
                    table.CheckConstraint("ck_business_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_services", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    telegram_username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_days_off",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: true),
                    date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_days_off", x => x.id);
                    table.CheckConstraint("ck_staff_days_off_one_kind", "(\n    weekday IS NOT NULL\n    AND date IS NULL\n)\nOR\n(\n    weekday IS NULL\n    AND date IS NOT NULL\n)");
                    table.CheckConstraint("ck_staff_days_off_weekday", "weekday IS NULL OR weekday BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "FK_staff_days_off_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_services", x => x.id);
                    table.UniqueConstraint("ak_staff_services_id_staff_member_id", x => new { x.id, x.staff_member_id });
                    table.CheckConstraint("ck_staff_services_duration_minutes", "duration_minutes > 0");
                    table.CheckConstraint("ck_staff_services_price", "price >= 0");
                    table.ForeignKey(
                        name: "FK_staff_services_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_services_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "working_hours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opens_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    closes_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    break_starts_at = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    break_ends_at = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_working_hours", x => x.id);
                    table.CheckConstraint("ck_working_hours_break", "(\n    break_starts_at IS NULL\n    AND break_ends_at IS NULL\n)\nOR\n(\n    break_starts_at IS NOT NULL\n    AND break_ends_at IS NOT NULL\n    AND break_starts_at < break_ends_at\n    AND break_starts_at >= opens_at\n    AND break_ends_at <= closes_at\n)");
                    table.CheckConstraint("ck_working_hours_interval", "opens_at < closes_at");
                    table.ForeignKey(
                        name: "FK_working_hours_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.id);
                    table.CheckConstraint("ck_bookings_creation_time", "created_at_utc < starts_at_utc");
                    table.CheckConstraint("ck_bookings_duration", "duration_minutes > 0");
                    table.CheckConstraint("ck_bookings_interval", "ends_at_utc > starts_at_utc");
                    table.CheckConstraint("ck_bookings_lifecycle", "(\n    status = 'Confirmed'\n    AND cancelled_at_utc IS NULL\n    AND completed_at_utc IS NULL\n)\nOR\n(\n    status = 'Cancelled'\n    AND cancelled_at_utc IS NOT NULL\n    AND completed_at_utc IS NULL\n    AND cancelled_at_utc >= created_at_utc\n    AND cancelled_at_utc < starts_at_utc\n)\nOR\n(\n    status = 'Completed'\n    AND completed_at_utc IS NOT NULL\n    AND cancelled_at_utc IS NULL\n    AND completed_at_utc >= ends_at_utc\n)");
                    table.CheckConstraint("ck_bookings_price", "price >= 0");
                    table.CheckConstraint("ck_bookings_snapshot_duration", "ends_at_utc =\n    starts_at_utc + duration_minutes * INTERVAL '1 minute'");
                    table.CheckConstraint("ck_bookings_status", "status IN ('Confirmed', 'Cancelled', 'Completed')");
                    table.ForeignKey(
                        name: "FK_bookings_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bookings_staff_services_staff_service_id_staff_member_id",
                        columns: x => new { x.staff_service_id, x.staff_member_id },
                        principalTable: "staff_services",
                        principalColumns: new[] { "id", "staff_member_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bookings_users_client_user_id",
                        column: x => x.client_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bookings_client_start",
                table: "bookings",
                columns: new[] { "client_user_id", "starts_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_staff_service_id_staff_member_id",
                table: "bookings",
                columns: new[] { "staff_service_id", "staff_member_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bookings_staff_start",
                table: "bookings",
                columns: new[] { "staff_member_id", "starts_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_bookings_client_request",
                table: "bookings",
                columns: new[] { "client_user_id", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_staff_days_off_staff_date",
                table: "staff_days_off",
                columns: new[] { "staff_member_id", "date" },
                unique: true,
                filter: "date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_staff_days_off_staff_weekday",
                table: "staff_days_off",
                columns: new[] { "staff_member_id", "weekday" },
                unique: true,
                filter: "weekday IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_staff_members_user_id",
                table: "staff_members",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_services_service_id",
                table: "staff_services",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ux_staff_services_staff_member_id_service_id",
                table: "staff_services",
                columns: new[] { "staff_member_id", "service_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_telegram_user_id",
                table: "users",
                column: "telegram_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_working_hours_staff_member_id",
                table: "working_hours",
                column: "staff_member_id",
                unique: true);
            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS btree_gist;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE bookings
                ADD CONSTRAINT ex_bookings_staff_confirmed_overlap
                EXCLUDE USING gist
                (
                    staff_member_id WITH =,
                    tstzrange(starts_at_utc, ends_at_utc, '[)') WITH &&
                )
                WHERE (status = 'Confirmed');

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "business_settings");

            migrationBuilder.DropTable(
                name: "staff_days_off");

            migrationBuilder.DropTable(
                name: "working_hours");

            migrationBuilder.DropTable(
                name: "staff_services");

            migrationBuilder.DropTable(
                name: "services");

            migrationBuilder.DropTable(
                name: "staff_members");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
