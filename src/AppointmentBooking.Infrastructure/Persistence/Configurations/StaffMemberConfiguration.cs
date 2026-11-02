using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class StaffMemberConfiguration
    : IEntityTypeConfiguration<StaffMember>
{
    public void Configure(EntityTypeBuilder<StaffMember> builder)
    {
        builder.ToTable("staff_members");

        builder.HasKey(staff => staff.Id);

        builder.Property(staff => staff.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(staff => staff.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(staff => staff.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(staff => staff.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.HasIndex(staff => staff.UserId)
            .IsUnique()
            .HasDatabaseName("ux_staff_members_user_id");

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<StaffMember>(staff => staff.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}