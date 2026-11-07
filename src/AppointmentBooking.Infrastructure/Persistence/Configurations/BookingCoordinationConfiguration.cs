using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class BookingCoordinationConfiguration : IEntityTypeConfiguration<BookingCoordination>
{
    public void Configure(EntityTypeBuilder<BookingCoordination> builder)
    {
        builder.ToTable("booking_coordination", table =>
        {
            table.HasCheckConstraint(
                "ck_booking_coordination_one_scope",
                "(business_settings_id IS NULL) <> (staff_member_id IS NULL)");
            table.HasCheckConstraint("ck_booking_coordination_version", "change_version >= 0");
        });

        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.BusinessSettingsId).HasColumnName("business_settings_id");
        builder.Property(row => row.StaffMemberId).HasColumnName("staff_member_id");
        builder.Property(row => row.ChangeVersion).HasColumnName("change_version").HasDefaultValue(0L);

        builder.HasOne<BusinessSettings>().WithOne()
            .HasForeignKey<BookingCoordination>(row => row.BusinessSettingsId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StaffMember>().WithOne()
            .HasForeignKey<BookingCoordination>(row => row.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
