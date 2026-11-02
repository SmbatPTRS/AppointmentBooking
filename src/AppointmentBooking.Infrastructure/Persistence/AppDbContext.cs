using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<BusinessSettings> BusinessSettings => Set<BusinessSettings>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<WorkingHours> WorkingHours => Set<WorkingHours>();
    public DbSet<StaffDayOff> StaffDaysOff => Set<StaffDayOff>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AppDbContext).Assembly);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Nested transaction boundaries are not supported.");
        }

        await using var transaction =
            await Database.BeginTransactionAsync(cancellationToken);

        var result = await operation(cancellationToken);

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}