using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class StaffServiceConfiguration
    : IEntityTypeConfiguration<StaffService>
{
    public void Configure(EntityTypeBuilder<StaffService> builder)
    {
        builder.ToTable("staff_services", table =>
        {
            table.HasCheckConstraint(
                "ck_staff_services_price",
                "price >= 0");

            table.HasCheckConstraint(
                "ck_staff_services_duration_minutes",
                "duration_minutes > 0");
        });

        builder.HasKey(offering => offering.Id);

        builder.Property(offering => offering.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(offering => offering.StaffMemberId)
            .HasColumnName("staff_member_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(offering => offering.ServiceId)
            .HasColumnName("service_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(offering => offering.Price)
            .HasColumnName("price")
            .HasPrecision(12, 2)
            .IsRequired();

        builder.Property(offering => offering.DurationMinutes)
            .HasColumnName("duration_minutes")
            .IsRequired();

        builder.Property(offering => offering.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.HasIndex(offering => new
            {
                offering.StaffMemberId,
                offering.ServiceId
            })
            .IsUnique()
            .HasDatabaseName("ux_staff_services_staff_member_id_service_id");

        builder.HasAlternateKey(offering => new
            {
                offering.Id,
                offering.StaffMemberId
            })
            .HasName("ak_staff_services_id_staff_member_id");

        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(offering => offering.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Service>()
            .WithMany()
            .HasForeignKey(offering => offering.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}