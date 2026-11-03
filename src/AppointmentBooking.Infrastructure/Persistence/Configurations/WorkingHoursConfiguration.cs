using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class WorkingHoursConfiguration
    : IEntityTypeConfiguration<WorkingHours>
{
    public void Configure(EntityTypeBuilder<WorkingHours> builder)
    {
        builder.ToTable("working_hours", table =>
        {
            table.HasCheckConstraint(
                "ck_working_hours_interval",
                "opens_at < closes_at");

            table.HasCheckConstraint(
                "ck_working_hours_break",
                """
                (
                    break_starts_at IS NULL
                    AND break_ends_at IS NULL
                )
                OR
                (
                    break_starts_at IS NOT NULL
                    AND break_ends_at IS NOT NULL
                    AND break_starts_at < break_ends_at
                    AND break_starts_at >= opens_at
                    AND break_ends_at <= closes_at
                )
                """);
        });

        builder.HasKey(hours => hours.Id);

        builder.Property(hours => hours.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(hours => hours.StaffMemberId)
            .HasColumnName("staff_member_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(hours => hours.OpensAt)
            .HasColumnName("opens_at")
            .HasColumnType("time without time zone")
            .IsRequired();

        builder.Property(hours => hours.ClosesAt)
            .HasColumnName("closes_at")
            .HasColumnType("time without time zone")
            .IsRequired();

        builder.Property(hours => hours.BreakStartsAt)
            .HasColumnName("break_starts_at")
            .HasColumnType("time without time zone")
            .IsRequired(false);

        builder.Property(hours => hours.BreakEndsAt)
            .HasColumnName("break_ends_at")
            .HasColumnType("time without time zone")
            .IsRequired(false);

        builder.HasIndex(hours => hours.StaffMemberId)
            .IsUnique()
            .HasDatabaseName("ux_working_hours_staff_member_id");

        builder.HasOne<StaffMember>()
            .WithOne()
            .HasForeignKey<WorkingHours>(hours => hours.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}