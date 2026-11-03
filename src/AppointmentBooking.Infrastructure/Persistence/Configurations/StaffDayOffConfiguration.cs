using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class StaffDayOffConfiguration
    : IEntityTypeConfiguration<StaffDayOff>
{
    public void Configure(EntityTypeBuilder<StaffDayOff> builder)
    {
        builder.ToTable("staff_days_off", table =>
        {
            table.HasCheckConstraint(
                "ck_staff_days_off_one_kind",
                """
                (
                    weekday IS NOT NULL
                    AND date IS NULL
                )
                OR
                (
                    weekday IS NULL
                    AND date IS NOT NULL
                )
                """);

            table.HasCheckConstraint(
                "ck_staff_days_off_weekday",
                "weekday IS NULL OR weekday BETWEEN 0 AND 6");
        });

        builder.HasKey(dayOff => dayOff.Id);

        builder.Property(dayOff => dayOff.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(dayOff => dayOff.StaffMemberId)
            .HasColumnName("staff_member_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(dayOff => dayOff.Weekday)
            .HasColumnName("weekday")
            .HasConversion<int>()
            .HasColumnType("integer")
            .IsRequired(false);

        builder.Property(dayOff => dayOff.Date)
            .HasColumnName("date")
            .HasColumnType("date")
            .IsRequired(false);

        builder.HasIndex(dayOff => new
            {
                dayOff.StaffMemberId,
                dayOff.Weekday
            })
            .IsUnique()
            .HasFilter("weekday IS NOT NULL")
            .HasDatabaseName("ux_staff_days_off_staff_weekday");

        builder.HasIndex(dayOff => new
            {
                dayOff.StaffMemberId,
                dayOff.Date
            })
            .IsUnique()
            .HasFilter("date IS NOT NULL")
            .HasDatabaseName("ux_staff_days_off_staff_date");

        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(dayOff => dayOff.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}