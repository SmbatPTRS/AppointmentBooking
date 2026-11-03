using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class BookingConfiguration
    : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("bookings", table =>
        {
            table.HasCheckConstraint(
                "ck_bookings_interval",
                "ends_at_utc > starts_at_utc");

            table.HasCheckConstraint(
                "ck_bookings_price",
                "price >= 0");

            table.HasCheckConstraint(
                "ck_bookings_duration",
                "duration_minutes > 0");

            table.HasCheckConstraint(
                "ck_bookings_snapshot_duration",
                """
                ends_at_utc =
                    starts_at_utc + duration_minutes * INTERVAL '1 minute'
                """);

            table.HasCheckConstraint(
                "ck_bookings_status",
                "status IN ('Confirmed', 'Cancelled', 'Completed')");

            table.HasCheckConstraint(
                "ck_bookings_creation_time",
                "created_at_utc < starts_at_utc");

            table.HasCheckConstraint(
                "ck_bookings_lifecycle",
                """
                (
                    status = 'Confirmed'
                    AND cancelled_at_utc IS NULL
                    AND completed_at_utc IS NULL
                )
                OR
                (
                    status = 'Cancelled'
                    AND cancelled_at_utc IS NOT NULL
                    AND completed_at_utc IS NULL
                    AND cancelled_at_utc >= created_at_utc
                    AND cancelled_at_utc < starts_at_utc
                )
                OR
                (
                    status = 'Completed'
                    AND completed_at_utc IS NOT NULL
                    AND cancelled_at_utc IS NULL
                    AND completed_at_utc >= ends_at_utc
                )
                """);
        });

        builder.HasKey(booking => booking.Id);

        builder.Property(booking => booking.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(booking => booking.ClientUserId)
            .HasColumnName("client_user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(booking => booking.StaffMemberId)
            .HasColumnName("staff_member_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(booking => booking.StaffServiceId)
            .HasColumnName("staff_service_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(booking => booking.RequestId)
            .HasColumnName("request_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(booking => booking.StartsAtUtc)
            .HasColumnName("starts_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(booking => booking.EndsAtUtc)
            .HasColumnName("ends_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(booking => booking.Price)
            .HasColumnName("price")
            .HasPrecision(12, 2)
            .IsRequired();

        builder.Property(booking => booking.DurationMinutes)
            .HasColumnName("duration_minutes")
            .IsRequired();

        builder.Property(booking => booking.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(booking => booking.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(booking => booking.CancelledAtUtc)
            .HasColumnName("cancelled_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.Property(booking => booking.CompletedAtUtc)
            .HasColumnName("completed_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        builder.HasIndex(booking => new
            {
                booking.ClientUserId,
                booking.RequestId
            })
            .IsUnique()
            .HasDatabaseName("ux_bookings_client_request");

        builder.HasIndex(booking => new
            {
                booking.StaffMemberId,
                booking.StartsAtUtc
            })
            .HasDatabaseName("ix_bookings_staff_start");

        builder.HasIndex(booking => new
            {
                booking.ClientUserId,
                booking.StartsAtUtc
            })
            .HasDatabaseName("ix_bookings_client_start");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(booking => booking.ClientUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(booking => booking.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StaffService>()
            .WithMany()
            .HasForeignKey(booking => new
            {
                booking.StaffServiceId,
                booking.StaffMemberId
            })
            .HasPrincipalKey(offering => new
            {
                offering.Id,
                offering.StaffMemberId
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}