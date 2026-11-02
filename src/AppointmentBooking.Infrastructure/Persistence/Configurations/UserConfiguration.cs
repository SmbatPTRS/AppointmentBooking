using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppointmentBooking.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(user => user.TelegramUserId)
            .HasColumnName("telegram_user_id")
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(user => user.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(user => user.TelegramUsername)
            .HasColumnName("telegram_username")
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(user => user.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(user => user.TelegramUserId)
            .IsUnique()
            .HasDatabaseName("ux_users_telegram_user_id");
    }
}