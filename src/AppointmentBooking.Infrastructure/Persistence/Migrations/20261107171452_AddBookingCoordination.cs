using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AppointmentBooking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "booking_coordination",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_settings_id = table.Column<int>(type: "integer", nullable: true),
                    staff_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    change_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_coordination", x => x.id);
                    table.CheckConstraint("ck_booking_coordination_one_scope", "(business_settings_id IS NULL) <> (staff_member_id IS NULL)");
                    table.CheckConstraint("ck_booking_coordination_version", "change_version >= 0");
                    table.ForeignKey(
                        name: "FK_booking_coordination_business_settings_business_settings_id",
                        column: x => x.business_settings_id,
                        principalTable: "business_settings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_coordination_staff_members_staff_member_id",
                        column: x => x.staff_member_id,
                        principalTable: "staff_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_booking_coordination_business_settings_id",
                table: "booking_coordination",
                column: "business_settings_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_coordination_staff_member_id",
                table: "booking_coordination",
                column: "staff_member_id",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO booking_coordination (business_settings_id)
                SELECT id FROM business_settings;

                INSERT INTO booking_coordination (staff_member_id)
                SELECT id FROM staff_members;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_coordination");
        }
    }
}
