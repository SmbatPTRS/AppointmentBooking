using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class BusinessSettingsConfiguration
    : IEntityTypeConfiguration<BusinessSettings>
{
    public void Configure(EntityTypeBuilder<BusinessSettings> builder)
    {
        builder.ToTable("business_settings", table =>
        {
            table.HasCheckConstraint(
                "ck_business_settings_singleton",
                "id = 1");
        });

        builder.HasKey(settings => settings.Id);

        builder.Property(settings => settings.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(settings => settings.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(settings => settings.StaffLabel)
            .HasColumnName("staff_label")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(settings => settings.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(settings => settings.CurrencyCode)
            .HasColumnName("currency_code")
            .HasMaxLength(3)
            .IsRequired();
    }
}